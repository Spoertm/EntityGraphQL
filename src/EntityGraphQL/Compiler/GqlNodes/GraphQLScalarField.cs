using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using EntityGraphQL.Compiler.Util;
using EntityGraphQL.Schema;

namespace EntityGraphQL.Compiler
{
    public class GraphQLScalarField : BaseGraphQLField
    {
        public GraphQLScalarField(ISchemaProvider schema, IField field, string name, Expression nextFieldContext, ParameterExpression? rootParameter, IGraphQLNode parentNode, IReadOnlyDictionary<string, object>? arguments)
            : base(schema, field, name, nextFieldContext, rootParameter, parentNode, arguments)
        {
        }

        public override bool HasServicesAtOrBelow(IEnumerable<GraphQLFragmentStatement> fragments)
        {
            return Field?.Services.Count > 0;
        }

        protected override Expression? GetFieldExpression(CompileContext compileContext, IServiceProvider? serviceProvider, List<GraphQLFragmentStatement> fragments, ParameterExpression? docParam, object? docVariables, ParameterExpression schemaContext, bool withoutServiceFields, Expression? replacementNextFieldContext, bool isRoot, bool contextChanged, ParameterReplacer replacer)
        {
            if (HasServices && withoutServiceFields)
                return null;

            var nextFieldContext = HandleBulkServiceResolver(compileContext, withoutServiceFields, NextFieldContext)!;

            (var result, _) = Field!.GetExpression(nextFieldContext, replacementNextFieldContext, ParentNode!, schemaContext, compileContext, Arguments, docParam, docVariables, Directives, contextChanged, replacer);

            if (result == null)
                return null;

            var newExpression = result;

            if (contextChanged && replacementNextFieldContext != null)
            {
                newExpression = ReplaceContext(replacementNextFieldContext!, isRoot, replacer, newExpression!);
            }
            newExpression = ProcessScalarExpression(newExpression, replacer);

            if (HasServices)
                compileContext.AddServices(Field.Services);
            return newExpression;
        }
    }
}