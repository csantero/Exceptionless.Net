﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Dependency;
using Exceptionless.Plugins;
using Exceptionless.Plugins.Default;
using Exceptionless.Models;
using Exceptionless.Models.Data;
using Exceptionless.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Exceptionless.Tests.Plugins {
    public class PluginTests {
        private readonly TestOutputWriter _writer;
        public PluginTests(ITestOutputHelper output) {
            _writer = new TestOutputWriter(output);
        }

        [Fact]
        public void ConfigurationDefaults_EnsureNoDuplicateTagsOrData() {
            var client = new ExceptionlessClient();
            var context = new EventPluginContext(client, new Event());

            var plugin = new ConfigurationDefaultsPlugin();
            plugin.Run(context);
            Assert.Equal(0, context.Event.Tags.Count);

            client.Configuration.DefaultTags.Add(Event.KnownTags.Critical);
            plugin.Run(context);
            Assert.Equal(1, context.Event.Tags.Count);
            Assert.Equal(0, context.Event.Data.Count);

            client.Configuration.DefaultData.Add("Message", new { Exceptionless = "Is Awesome!" });
            for (int index = 0; index < 2; index++) {
                plugin.Run(context);
                Assert.Equal(1, context.Event.Tags.Count);
                Assert.Equal(1, context.Event.Data.Count);
            }
        }

         [Fact]
        public void ConfigurationDefaults_IgnoredProperties() {
            var client = new ExceptionlessClient();
            client.Configuration.DefaultData.Add("Message", "Test");

            var context = new EventPluginContext(client, new Event());
            var plugin = new ConfigurationDefaultsPlugin();
            plugin.Run(context);
            Assert.Equal(1, context.Event.Data.Count);
            Assert.Equal("Test", context.Event.Data["Message"]);
            
            client.Configuration.AddDataExclusions("Ignore*");
            client.Configuration.DefaultData.Add("Ignored", "Test");
            plugin.Run(context);
            Assert.Equal(1, context.Event.Data.Count);
            Assert.Equal("Test", context.Event.Data["Message"]);
        }

        [Fact]
        public void ErrorPlugin_IgnoredProperties() {
            var exception = new MyApplicationException("Test") {
                IgnoredProperty = "Test",
                RandomValue = "Test"
            };
            
            var errorPlugins = new List<IEventPlugin> {
                new ErrorPlugin(),
                new SimpleErrorPlugin()
            };

            foreach (var plugin in errorPlugins) {
                var client = new ExceptionlessClient();
                var context = new EventPluginContext(client, new Event());
                context.ContextData.SetException(exception);

                plugin.Run(context);
                IData error = context.Event.GetError() as IData ?? context.Event.GetSimpleError();
                Assert.NotNull(error);
                Assert.True(error.Data.ContainsKey(Error.KnownDataKeys.ExtraProperties));
                var json = error.Data[Error.KnownDataKeys.ExtraProperties] as string;
                Assert.Equal("{\"ignored_property\":\"Test\",\"random_value\":\"Test\"}", json);

                client.Configuration.AddDataExclusions("Ignore*");
                context = new EventPluginContext(client, new Event());
                context.ContextData.SetException(exception);

                plugin.Run(context);
                error = context.Event.GetError() as IData ?? context.Event.GetSimpleError();
                Assert.NotNull(error);
                Assert.True(error.Data.ContainsKey(Error.KnownDataKeys.ExtraProperties));
                json = error.Data[Error.KnownDataKeys.ExtraProperties] as string;
                Assert.Equal("{\"random_value\":\"Test\"}", json);
            }
        }

        [Theory(Skip = "TODO: This needs to be skipped until the client is sending session start and end.")]
        [InlineData(Event.KnownTypes.Error)]
        [InlineData(Event.KnownTypes.FeatureUsage)]
        [InlineData(Event.KnownTypes.Log)]
        [InlineData(Event.KnownTypes.NotFound)]
        [InlineData(Event.KnownTypes.SessionEnd)]
        public void EnvironmentInfo_IncorrectEventType(string eventType) {
            var client = new ExceptionlessClient();
            var context = new EventPluginContext(client, new Event { Type = eventType });

            var plugin = new EnvironmentInfoPlugin();
            plugin.Run(context);
            Assert.Equal(0, context.Event.Data.Count);
        }

        [Fact]
        public void EnvironmentInfo_CanRunInParallel() {
            var client = new ExceptionlessClient();
            var ev = new Event { Type = Event.KnownTypes.Session };
            var plugin = new EnvironmentInfoPlugin();

            Parallel.For(0, 10000, i => {
                var context = new EventPluginContext(client, ev);
                plugin.Run(context);
                Assert.Equal(1, context.Event.Data.Count);
                Assert.NotNull(context.Event.Data[Event.KnownDataKeys.EnvironmentInfo]);
            });
        }

        [Fact]
        public void EnvironmentInfo_ShouldAddSessionStart() {
            var client = new ExceptionlessClient();
            var context = new EventPluginContext(client, new Event { Type = Event.KnownTypes.Session });
         
            var plugin = new EnvironmentInfoPlugin();
            plugin.Run(context);
            Assert.Equal(1, context.Event.Data.Count);
            Assert.NotNull(context.Event.Data[Event.KnownDataKeys.EnvironmentInfo]);
        }

        [Fact]
        public void CanCancel() {
            var client = new ExceptionlessClient();
            foreach (var plugin in client.Configuration.Plugins)
                client.Configuration.RemovePlugin(plugin.Key);

            client.Configuration.AddPlugin("cancel", 1, ctx => ctx.Cancel = true);
            client.Configuration.AddPlugin("add-tag", 2, ctx => ctx.Event.Tags.Add("Was Not Canceled"));

            var context = new EventPluginContext(client, new Event());
            EventPluginManager.Run(context);
            Assert.True(context.Cancel);
            Assert.Equal(0, context.Event.Tags.Count);
        }

        [Fact]
        public void ShouldUseReferenceIds() {
            var client = new ExceptionlessClient();
            foreach (var plugin in client.Configuration.Plugins)
                client.Configuration.RemovePlugin(plugin.Key);

            var context = new EventPluginContext(client, new Event { Type = Event.KnownTypes.Error });
            EventPluginManager.Run(context);
            Assert.Null(context.Event.ReferenceId);

            client.Configuration.UseReferenceIds();
            context = new EventPluginContext(client, new Event { Type = Event.KnownTypes.Error });
            EventPluginManager.Run(context);
            Assert.NotNull(context.Event.ReferenceId);
        }

        [Fact]
        public void PrivateInformation_WillSetIdentity() {
            var client = new ExceptionlessClient();
            var plugin = new SetEnvironmentUserPlugin();

            var context = new EventPluginContext(client, new Event { Type = Event.KnownTypes.Log, Message = "test" });
            plugin.Run(context);

            var user = context.Event.GetUserIdentity();
            Assert.Equal(Environment.UserName, user?.Identity);
        }
        
        [Fact]
        public void PrivateInformation_WillNotUpdateIdentity() {
            var client = new ExceptionlessClient();
            var plugin = new SetEnvironmentUserPlugin();

            var ev = new Event { Type = Event.KnownTypes.Log, Message = "test" };
            ev.SetUserIdentity(null, "Blake");
            var context = new EventPluginContext(client, ev);
            plugin.Run(context);

            var user = context.Event.GetUserIdentity();
            Assert.Null(user?.Identity);
            Assert.Equal("Blake", user?.Name);
        }


        [Fact]
        public void LazyLoadAndRemovePlugin() {
            var configuration = new ExceptionlessConfiguration(DependencyResolver.Default);
            foreach (var plugin in configuration.Plugins)
                configuration.RemovePlugin(plugin.Key);

            configuration.AddPlugin<ThrowIfInitializedTestPlugin>();
            configuration.RemovePlugin<ThrowIfInitializedTestPlugin>();
        }

        private class ThrowIfInitializedTestPlugin : IEventPlugin, IDisposable {
            public ThrowIfInitializedTestPlugin() {
                throw new ApplicationException("Plugin shouldn't be constructed");
            }

            public void Run(EventPluginContext context) {}
            
            public void Dispose() {
                throw new ApplicationException("Plugin shouldn't be created or disposed");
            }
        }

        [Fact]
        public void CanDisposePlugin() {
            var configuration = new ExceptionlessConfiguration(DependencyResolver.Default);
            foreach (var plugin in configuration.Plugins)
                configuration.RemovePlugin(plugin.Key);

            Assert.Equal(0, CounterTestPlugin.ConstructorCount);
            Assert.Equal(0, CounterTestPlugin.RunCount);
            Assert.Equal(0, CounterTestPlugin.DisposeCount);

            configuration.AddPlugin<CounterTestPlugin>();
            configuration.AddPlugin<CounterTestPlugin>();

            for (int i = 0; i < 2; i++) {
                foreach (var pluginRegistration in configuration.Plugins)
                    pluginRegistration.Plugin.Run(new EventPluginContext(new ExceptionlessClient(), new Event()));
            }

            configuration.RemovePlugin<CounterTestPlugin>();
            configuration.RemovePlugin<CounterTestPlugin>();


            Assert.Equal(1, CounterTestPlugin.ConstructorCount);
            Assert.Equal(2, CounterTestPlugin.RunCount);
            Assert.Equal(1, CounterTestPlugin.DisposeCount);
        }

        public class CounterTestPlugin : IEventPlugin, IDisposable {
            public static byte ConstructorCount = 0;
            public static byte RunCount = 0;
            public static byte DisposeCount = 0;

            public CounterTestPlugin() {
                ConstructorCount++;
            }

            public void Run(EventPluginContext context) {
                RunCount++;
            }
            
            public void Dispose() {
                DisposeCount++;
            }
        }

        [Fact]
        public void VerifyPriority() {
            var config = new ExceptionlessConfiguration(DependencyResolver.CreateDefault());
            foreach (var plugin in config.Plugins)
                config.RemovePlugin(plugin.Key);

            Assert.Equal(0, config.Plugins.Count());
            config.AddPlugin<EnvironmentInfoPlugin>();
            config.AddPlugin<PluginWithPriority11>();
            config.AddPlugin<PluginWithNoPriority>();
            config.AddPlugin("version", 1, ctx => ctx.Event.SetVersion("1.0.0.0"));
            config.AddPlugin("version2", 2, ctx => ctx.Event.SetVersion("1.0.0.0"));
            config.AddPlugin("version3", 3, ctx => ctx.Event.SetVersion("1.0.0.0"));

            var plugins = config.Plugins.ToArray();
            Assert.Equal(typeof(PluginWithNoPriority), plugins[0].Plugin.GetType());
            Assert.Equal("version", plugins[1].Key);
            Assert.Equal("version2", plugins[2].Key);
            Assert.Equal("version3", plugins[3].Key);
            Assert.Equal(typeof(PluginWithPriority11), plugins[4].Plugin.GetType());
            Assert.Equal(typeof(EnvironmentInfoPlugin), plugins[5].Plugin.GetType());
        }

        [Fact]
        public void ViewPriority() {
            var config = new ExceptionlessConfiguration(DependencyResolver.CreateDefault());
            foreach (var plugin in config.Plugins)
                _writer.WriteLine(plugin);
        }

        public class PluginWithNoPriority : IEventPlugin {
            public void Run(EventPluginContext context) {}
        }

        [Priority(11)]
        public class PluginWithPriority11 : IEventPlugin {
            public void Run(EventPluginContext context) {}
        }
        
        public class MyApplicationException : ApplicationException {
            public MyApplicationException(string message) : base(message) {}

            public string IgnoredProperty { get; set; }

            public string RandomValue { get; set; }
        }
    }
}