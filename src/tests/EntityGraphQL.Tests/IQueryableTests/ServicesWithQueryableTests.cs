using System.Linq;
using EntityGraphQL.Extensions;
using EntityGraphQL.Schema;
using Xunit;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using static EntityGraphQL.Tests.ServiceFieldTests;
using System;

namespace EntityGraphQL.Tests.IQueryableTests
{
    public class ServicesWithQueryableTests
    {

        [Fact]
        public void TestServiceFieldWithQueryable()
        {
            var schema = SchemaBuilder.FromObject<TestDbContext>();

            schema.AddType<ProjectConfig>("Config").AddAllFields();

            schema.Type<Movie>().AddField("config", "Get configs if they exists")
                .ResolveWithService<ConfigService>((p, srv) => srv.Get(p.Id))
                .IsNullable(false);
            schema.Type<Movie>().AddField("mainActor", p => p.Actors.FirstOrDefault(), "Actor");

            var gql = new QueryRequest
            {
                Query = @"{
                     movies {
                         config { type }
                         mainActor { name }
                     }
                 }"
            };

            var serviceCollection = new ServiceCollection();
            var srv = new ConfigService();
            serviceCollection.AddSingleton(srv);
            using var factory = new TestDbContextFactory();
            var data = factory.CreateContext();
            serviceCollection.AddSingleton(data);
            var serviceProvider = serviceCollection.BuildServiceProvider();
            data.Database.EnsureCreated();
            data.Movies.AddRange(
                new Movie { Id = 10, Name = "A New Hope", Actors = new List<Actor> { new() { Id = 1, Name = "Alec Guinness" }, new() { Id = 2, Name = "Mark Hamill" } } });
            data.SaveChanges();

            var res = schema.ExecuteRequest(gql, serviceProvider, null);
            Assert.Null(res.Errors);
            var movies = (dynamic)res.Data["movies"];
            Type movieType = Enumerable.First(movies).GetType();
            Assert.Equal(2, movieType.GetFields().Length);
            Assert.Equal("config", movieType.GetFields()[0].Name);
            // null check should not cause multiple calls
            Assert.Equal(1, srv.CallCount);
        }

        [Fact]
        public void TestGenericMethodToUpdateType()
        {
            var schema = SchemaBuilder.FromObject<TestDbContext>();

            AddExternalIdentifierProperty<Movie>(schema);

            var gql = new QueryRequest
            {
                Query = @"{
                    movies {
                        id
                        externalIdentifiers {
                            id
                            entityName
                        }
                    }
                 }"
            };

            var serviceCollection = new ServiceCollection();
            using var factory = new TestDbContextFactory();
            var data = factory.CreateContext();
            serviceCollection.AddSingleton(data);
            var serviceProvider = serviceCollection.BuildServiceProvider();
            data.Movies.AddRange(
                new Movie { Id = 10, Name = "A New Hope", Actors = new List<Actor> { new Actor { Id = 1, Name = "Alec Guinness" }, new Actor { Id = 2, Name = "Mark Hamill" } } });
            data.SaveChanges();

            var res = schema.ExecuteRequest(gql, serviceProvider, null);
            Assert.Null(res.Errors);
            var movies = (dynamic)res.Data["movies"];
        }

        [Fact]
        public void TestServiceBackToDbManyToOne()
        {
            // { serviceField { dbField { childField { field } } } }
            var schema = SchemaBuilder.FromObject<TestDbContext>();
            schema.AddType<ProjectConfig>("Config").AddAllFields();

            schema.UpdateType<ProjectConfig>(config =>
            {
                // connect back to the main query types
                config.AddField("movie", "Get movie")
                    .ResolveWithService<TestDbContext>((c, db) => db.Movies.Where(m => m.Id == c.Id).FirstOrDefault());
            });

            schema.Query().AddField("configs", "Get configs")
                .ResolveWithService<ConfigService>((p, srv) => srv.GetList(3, 100))
                .IsNullable(false);
            var gql = new QueryRequest
            {
                Query = @"{
                    configs {
                        id
                        movie {
                            director { name }
                        }
                    }
                 }"
            };

            var serviceCollection = new ServiceCollection();
            using var factory = new TestDbContextFactory();
            var data = factory.CreateContext();
            serviceCollection.AddTransient((sp) => factory.CreateContext());
            serviceCollection.AddSingleton(new ConfigService());
            var serviceProvider = serviceCollection.BuildServiceProvider();
            data.Directors.AddRange(
                new Director { Id = 31, Name = "George Lucas" },
                new Director { Id = 32, Name = "George Lucas1" },
                new Director { Id = 33, Name = "George Lucas2" });
            data.Movies.AddRange(
                new Movie { Id = 100, Name = "A New Hope", DirectorId = 31 },
                new Movie { Id = 101, Name = "A New Hope1", DirectorId = 32 },
                new Movie { Id = 102, Name = "A New Hope2", DirectorId = 33 });
            data.SaveChanges();

            var res = schema.ExecuteRequest(gql, serviceProvider, null);
            Assert.Null(res.Errors);
            var configs = (dynamic)res.Data["configs"];
            // If correctly loaded by EF these should not be null
            // select should be inserted before the .FirstOrDefault()
            Assert.NotNull(configs[0].movie.director);
            Assert.NotNull(configs[1].movie.director);
            Assert.NotNull(configs[2].movie.director);
        }

        private static void AddExternalIdentifierProperty<TEntityType>(SchemaProvider<TestDbContext> schema) where TEntityType : IEntityWithId
        {
            schema.Type<TEntityType>()
                .AddField("externalIdentifiers", new
                {
                    externalIdName = default(string)
                }, "External IDs")
                .ResolveWithService<TestDbContext>((movie, args, context) =>
                    context.ExternalIdentifiers
                        .WhereWhen(ei => ei.ExternalIdName == args.externalIdName, !string.IsNullOrWhiteSpace(args.externalIdName))
                        .Where(ei => ei.EntityName == context.GetEntityTableName<TEntityType>() && ei.EntityId == movie.Id)
                );
        }
    }

    internal static class ExternalIdentiferExtensions
    {
        internal static string GetEntityTableName<TEntity>(this TestDbContext context) => context.Model.FindEntityType(typeof(TEntity)).Name;
    }
}
