---
sidebar_position: 11
---

# Upgrading from 4.x to 5.x

EntityGraphQL aims to respect [Semantic Versioning](https://semver.org/), meaning version 5.0.0 contains breaking changes. Below highlights those changes and the impact to those coming from version 3.x.

:::tip
You can see the full changelog which includes other changes and bug fixes as well as links back to GitHub issues/MRs with more information [here on GitHub](https://github.com/EntityGraphQL/EntityGraphQL/blob/master/CHANGELOG.md).
:::

## Changes to Method Argument Reflection

Previously if `AutoCreateInputTypes` was enabled we didn't know if a parameter should be a GraphQL argument or an injected service. This has been refactored to be predictable. 

`AutoCreateInputTypes` now default to true and you may have to add some attributes to your parameters or classes.
`[GraphQLInputType]` will include the parameter as an argument and use the type as an input type. `[GraphQLArguments]` will flatten the properties of that parameter type into  many arguments in the schema.

1. First all scalar / non-complex types will be added at arguments in the schema.

2. If parameter type or enum type is already in the schema it will be added at an argument.

2. Any argument or type with `GraphQLInputTypeAttribute` or `GraphQLArgumentsAttribute` found will be added as schema arguments.

3. If no attributes are found it will assume they are services and not add them to the schema. *I.e. Label your arguments with the attributes or add them to the schema beforehand.*

`AutoCreateInputTypes` now only controls if the type of the argument should be added to the schema.

## `IExposableException` removed

Interface `IExposableException` has been removed. Use the existing `SchemaBuilderSchemaOptions.AllowedExceptions` property to define which exceptions are rendered into the results. Or mark your exceptions with the `AllowedExceptionAttribute` to have exception details in the results when `SchemaBuilderSchemaOptions.IsDevelopment` is `false`.


## `IDirectiveProcessor` updated

- `IDirectiveProcessor.On` renamed to `IDirectiveProcessor.Location`
- `IDirectiveProcessor.ProcessField()` removed, use `IDirectiveProcessor.VisitNode`
- `IDirectiveProcessor.ProcessExpression()` Has been removed. You can build a new `IGraphQLNode` in `VisitNode` to make changes to the graph

## `SchemaBuilderMethodOptions` removed

- `AutoCreateInputTypes` has been moved to `SchemaBuilderOptions` and is now defaulted to `true`.
- `AddNonAttributedMethods` has been move to `SchemaBuilderOptions.AddNonAttributedMethodsInControllers`