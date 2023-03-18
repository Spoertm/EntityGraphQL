using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using EntityGraphQL.Compiler;
using EntityGraphQL.Compiler.Util;
using EntityGraphQL.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace EntityGraphQL.Schema
{
    /// <summary>
    /// Respresents a GraphQL field backed by a method in dotnet. This is used for "controller" type fields. e.g. Mutations and Subscriptions.
    /// This is not used for query fields.
    /// 
    /// The way we execute this field is different to a query field with is built inline to an expression.
    /// 
    /// A MethodField is the top level field in a mutation/subscription and maps to a dotnet method. The result is projected back into the query graph.
    /// </summary>
    public abstract class MethodField : BaseField
    {
        public override GraphQLQueryFieldType FieldType { get; }
        protected MethodInfo Method { get; set; }
        public bool IsAsync { get; protected set; }

        private readonly Dictionary<string, Type> flattenArgmentTypes = new();

        public MethodField(ISchemaProvider schema, ISchemaType fromType, string methodName, GqlTypeInfo returnType, MethodInfo method, string description, RequiredAuthorization requiredAuth, bool isAsync, SchemaBuilderOptions options)
            : base(schema, fromType, methodName, description, returnType)
        {
            Services = new List<Type>();
            Method = method;
            RequiredAuthorization = requiredAuth;
            IsAsync = isAsync;

            foreach (var item in method.GetParameters())
            {
                if (GraphQLIgnoreAttribute.ShouldIgnoreMemberFromInput(item))
                    continue;

                var inputType = item.ParameterType.IsEnumerableOrArray() ? item.ParameterType.GetEnumerableOrArrayType()! : item.ParameterType;
                if (inputType.IsNullableType())
                    inputType = inputType.GetGenericArguments()[0];

                if (inputType == typeof(GraphQLValidator))
                    continue;

                // primitive types are arguments or types already known in the schema
                var shouldBeAddedAsArg = item.ParameterType.IsPrimitive || (schema.HasType(inputType) && (schema.Type(inputType).IsInput || schema.Type(inputType).IsScalar || schema.Type(inputType).IsEnum));

                // if hasServices then we expect attributes
                var argumentsAttr = item.GetCustomAttribute<GraphQLArgumentsAttribute>() ?? item.ParameterType.GetTypeInfo().GetCustomAttribute<GraphQLArgumentsAttribute>();
                var inlineArgumentAttr = item.GetCustomAttribute<GraphQLInputTypeAttribute>() ?? item.ParameterType.GetTypeInfo().GetCustomAttribute<GraphQLInputTypeAttribute>();
                if (!shouldBeAddedAsArg && argumentsAttr == null && inlineArgumentAttr == null)
                    continue; // services are not arguments in the schema

                if (argumentsAttr != null)
                {
                    FlattenArguments(item.ParameterType, schema, options);
                    flattenArgmentTypes.Add(item.Name!, item.ParameterType);
                }
                else
                {
                    Arguments.Add(Schema.SchemaFieldNamer(item.Name!), ArgType.FromParameter(schema, item, item.DefaultValue));

                    if (!schema.HasType(inputType) && options.AutoCreateInputTypes)
                    {
                        AddInputTypesInArguments(schema, options.AutoCreateInputTypes, inputType);
                    }
                }
            }
        }

        private void FlattenArguments(Type argType, ISchemaProvider schema, SchemaBuilderOptions options)
        {
            foreach (var item in argType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (GraphQLIgnoreAttribute.ShouldIgnoreMemberFromInput(item))
                    continue;
                Arguments.Add(Schema.SchemaFieldNamer(item.Name), ArgType.FromProperty(schema, item, null));
                AddInputTypesInArguments(schema, options.AutoCreateInputTypes, item.PropertyType);

            }
            foreach (var item in argType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (GraphQLIgnoreAttribute.ShouldIgnoreMemberFromInput(item))
                    continue;
                Arguments.Add(Schema.SchemaFieldNamer(item.Name), ArgType.FromField(schema, item, null));
                AddInputTypesInArguments(schema, options.AutoCreateInputTypes, item.FieldType);
            }
        }

        private static void AddInputTypesInArguments(ISchemaProvider schema, bool autoAddInputTypes, Type propType)
        {
            var inputType = propType.IsEnumerableOrArray() ? propType.GetEnumerableOrArrayType()! : propType.IsNullableType() ? propType.GetGenericArguments()[0] : propType;
            if (autoAddInputTypes && !schema.HasType(inputType))
                schema.AddInputType(inputType, GetTypeName(inputType), null).AddAllFields();
        }

        private static string GetTypeName(Type inputType)
        {
            if (inputType.IsGenericType)
            {
                var name = inputType.Name.Split('`')[0];
                var genericArgs = string.Join("", inputType.GetGenericArguments().Select(t => GetTypeName(t)));
                return $"{name}{genericArgs}";
            }
            return inputType.Name;
        }

        public virtual async Task<object?> CallAsync(object? context, IReadOnlyDictionary<string, object>? gqlRequestArgs, GraphQLValidator validator, IServiceProvider? serviceProvider, ParameterExpression? variableParameter, object? docVariables)
        {
            if (context == null)
                return null;

            // args in the mutation method - may be arguments in the graphql schema, services injected
            var allArgs = new List<object>();
            var argsToValidate = new List<object>();
            object? argInstance = null;
            var validationErrors = new List<string>();

            // add parameters and any DI services
            foreach (var p in Method.GetParameters())
            {
                if (p.GetCustomAttribute<GraphQLArgumentsAttribute>() != null || p.ParameterType.GetTypeInfo().GetCustomAttribute<GraphQLArgumentsAttribute>() != null)
                {
                    var argType = flattenArgmentTypes[p.Name!];
                    argInstance = ArgumentUtil.BuildArgumentsObject(Schema, Name, this, gqlRequestArgs ?? new Dictionary<string, object>(), Arguments.Values, argType, variableParameter, docVariables, validationErrors)!;
                    allArgs.Add(argInstance);
                }
                else if (gqlRequestArgs != null && gqlRequestArgs.ContainsKey(p.Name!))
                {
                    var argField = Arguments[p.Name!];
                    var value = ArgumentUtil.BuildArgumentFromMember(Schema, gqlRequestArgs, argField.Name, argField.RawType, argField.DefaultValue, validationErrors);
                    if (docVariables != null)
                    {
                        if (value is Expression and not null)
                        {
                            value = Expression.Lambda((Expression)value, variableParameter!).Compile().DynamicInvoke(new[] { docVariables });
                        }
                    }
                    // this could be int to RequiredField<int>
                    if (value != null && value.GetType() != argField.RawType)
                    {
                        value = ExpressionUtil.ChangeType(value, argField.RawType, Schema);
                    }

                    argField.Validate(value, p.Name!, validationErrors);

                    allArgs.Add(value!);
                    argsToValidate.Add(value!);
                }
                else if (p.ParameterType == context.GetType())
                {
                    allArgs.Add(context);
                }
                // todo we should put this in the IServiceCollection actually...
                else if (p.ParameterType == typeof(GraphQLValidator))
                {
                    allArgs.Add(validator);
                }
                else if (serviceProvider != null)
                {
                    var service = serviceProvider.GetService(p.ParameterType) ?? throw new EntityGraphQLExecutionException($"Service {p.ParameterType.Name} not found for dependency injection for mutation {Method.Name}");
                    allArgs.Add(service);
                }
                else
                {
                    var argField = Arguments[p.Name!];
                    if (argField.DefaultValue != null)
                    {
                        allArgs.Add(argField.DefaultValue);
                    }
                }
            }

            if (ArgumentValidators.Count > 0)
            {
                var validatorContext = new ArgumentValidatorContext(this, argInstance ?? argsToValidate, Method);
                foreach (var argValidator in ArgumentValidators)
                {
                    argValidator(validatorContext);
                }
                if (validatorContext.Errors != null && validatorContext.Errors.Count > 0)
                {
                    validationErrors.AddRange(validatorContext.Errors);
                }
            }

            if (validationErrors.Count > 0)
            {
                throw new EntityGraphQLValidationException(validationErrors);
            }

            object? instance = null;
            // we create an instance _per request_ injecting any parameters to the constructor
            // We kind of treat a mutation class like an asp.net controller
            // and we do not want to register them in the service provider to avoid the same issues controllers would have
            // with different lifetime objects
            if (instance == null)
            {
                instance = serviceProvider != null ?
                    ActivatorUtilities.CreateInstance(serviceProvider, Method.DeclaringType!) :
                    Activator.CreateInstance(Method.DeclaringType!);
            }

            object? result;
            if (IsAsync)
            {
                result = await (dynamic?)Method.Invoke(instance, allArgs.Any() ? allArgs.ToArray() : null);
            }
            else
            {
                try
                {
                    result = Method.Invoke(instance, allArgs.ToArray());
                }
                catch (TargetInvocationException ex)
                {
                    if (ex.InnerException != null)
                        throw ex.InnerException;
                    throw;
                }
            }
            return result;
        }

        public override (Expression? expression, ParameterExpression? argumentParam) GetExpression(Expression fieldExpression, Expression? fieldContext, IGraphQLNode? parentNode, ParameterExpression? schemaContext, CompileContext? compileContext, IReadOnlyDictionary<string, object> args, ParameterExpression? docParam, object? docVariables, IEnumerable<GraphQLDirective> directives, bool contextChanged, ParameterReplacer replacer)
        {
            var result = fieldExpression;

            if (schemaContext != null && result != null)
            {
                result = replacer.ReplaceByType(result, schemaContext.Type, schemaContext);
            }
            return (result, null);
        }
    }
}