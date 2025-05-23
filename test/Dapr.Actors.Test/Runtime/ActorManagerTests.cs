﻿// ------------------------------------------------------------------------
// Copyright 2021 The Dapr Authors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ------------------------------------------------------------------------

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Actors.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dapr.Actors.Runtime
{
    public sealed class ActorManagerTests
    {
        private ActorManager CreateActorManager(Type type, ActorActivator activator = null)
        {
            var registration = new ActorRegistration(ActorTypeInformation.Get(type, actorTypeName: null));
            var interactor = new DaprHttpInteractor(clientHandler: null, "http://localhost:3500", apiToken: null, requestTimeout: null);
            return new ActorManager(registration, activator ?? new DefaultActorActivator(), JsonSerializerDefaults.Web, false, NullLoggerFactory.Instance, ActorProxy.DefaultProxyFactory, interactor);
        }

        [Fact]
        public async Task ActivateActorAsync_CreatesActorAndCallsActivateLifecycleMethod()
        {
            var manager = CreateActorManager(typeof(TestActor));

            var id = ActorId.CreateRandom();
            await manager.ActivateActorAsync(id);

            Assert.True(manager.TryGetActorAsync(id, out var actor));
            Assert.True(Assert.IsType<TestActor>(actor).IsActivated);
        }

        [Fact]
        public async Task ActivateActorAsync_CanActivateMultipleActors()
        {
            var manager = CreateActorManager(typeof(TestActor));

            await manager.ActivateActorAsync(new ActorId("1"));
            Assert.True(manager.TryGetActorAsync(new ActorId("1"), out var actor1));

            await manager.ActivateActorAsync(new ActorId("2"));
            Assert.True(manager.TryGetActorAsync(new ActorId("2"), out var actor2));

            Assert.NotSame(actor1, actor2);
        }

        [Fact]
        public async Task ActivateActorAsync_UsesActivator()
        {
            var activator = new TestActivator();

            var manager = CreateActorManager(typeof(TestActor), activator);

            var id = ActorId.CreateRandom();
            await manager.ActivateActorAsync(id);

            Assert.Equal(1, activator.CreateCallCount);
        }

        [Fact]
        public async Task ActivateActorAsync_DoubleActivation_DeactivatesNewActor()
        {
            // We have to use the activator to observe the behavior here. We don't
            // have a way to interact with the "new" actor that gets destroyed immediately.
            var activator = new TestActivator();

            var manager = CreateActorManager(typeof(TestActor), activator);

            var id = ActorId.CreateRandom();
            await manager.ActivateActorAsync(id);

            Assert.True(manager.TryGetActorAsync(id, out var original));

            // It's a double-activation! We don't expect the runtime to do this, but the code
            // handles it.
            await manager.ActivateActorAsync(id);

            // Still holding the original actor
            Assert.True(manager.TryGetActorAsync(id, out var another));
            Assert.Same(original, another);
            Assert.False(Assert.IsType<TestActor>(another).IsDeactivated);
            Assert.False(Assert.IsType<TestActor>(another).IsDisposed);

            // We should have seen 2 create operations and 1 delete
            Assert.Equal(2, activator.CreateCallCount);
            Assert.Equal(1, activator.DeleteCallCount);
        }

        [Fact]
        public async Task ActivateActorAsync_ExceptionDuringActivation_ActorNotStoredAndDeleted()
        {
            var activator = new TestActivator();

            var manager = CreateActorManager(typeof(ThrowsDuringOnActivateAsync), activator);

            var id = ActorId.CreateRandom();

            await Assert.ThrowsAsync<InvalidTimeZoneException>(async () =>
            {
                await manager.ActivateActorAsync(id);
            });

            Assert.False(manager.TryGetActorAsync(id, out _));
            Assert.Equal(1, activator.DeleteCallCount);
        }

        [Fact]
        public async Task DectivateActorAsync_DeletesActorAndCallsDeactivateLifecycleMethod()
        {
            var manager = CreateActorManager(typeof(TestActor));

            var id = ActorId.CreateRandom();
            await manager.ActivateActorAsync(id);
            
            Assert.True(manager.TryGetActorAsync(id, out var actor));
            await manager.DeactivateActorAsync(id);

            Assert.True(Assert.IsType<TestActor>(actor).IsDeactivated);
            Assert.True(Assert.IsType<TestActor>(actor).IsDisposed);
        }

        [Fact]
        public async Task DeactivateActorAsync_ItsOkToDeactivateNonExistentActor()
        {
            var manager = CreateActorManager(typeof(TestActor));

            var id = ActorId.CreateRandom();
            Assert.False(manager.TryGetActorAsync(id, out _));
            await manager.DeactivateActorAsync(id);
        }

        [Fact]
        public async Task DeactivateActorAsync_UsesActivator()
        {
            var activator = new TestActivator();

            var manager = CreateActorManager(typeof(TestActor), activator);

            var id = ActorId.CreateRandom();
            await manager.ActivateActorAsync(id);
            await manager.DeactivateActorAsync(id);

            Assert.Equal(1, activator.CreateCallCount);
            Assert.Equal(1, activator.DeleteCallCount);
        }

        [Fact]
        public async Task DeactivateActorAsync_ExceptionDuringDeactivation_ActorIsRemovedAndDeleted()
        {
            var activator = new TestActivator();

            var manager = CreateActorManager(typeof(ThrowsDuringOnDeactivateAsync), activator);

            var id = ActorId.CreateRandom();
            await manager.ActivateActorAsync(id);
            Assert.True(manager.TryGetActorAsync(id, out _));

            await Assert.ThrowsAsync<InvalidTimeZoneException>(async () =>
            {
                await manager.DeactivateActorAsync(id);
            });

            Assert.False(manager.TryGetActorAsync(id, out _));
            Assert.Equal(1, activator.DeleteCallCount);
        }

        [Fact]
        public async Task DeserializeTimer_Period_Iso8601_Time()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"period\": \"0h0m7s10ms\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.FromSeconds(7).Add(TimeSpan.FromMilliseconds(10)), result.Period);
        }

        [Fact]
        public async Task DeserializeTimer_Period_DaprFormat_Every()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"period\": \"@every 15s\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.FromSeconds(15), result.Period);
        }
        
        [Fact]
        public async Task DeserializeTimer_Period_DaprFormat_Every2()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"period\": \"@every 3h2m15s\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(2)).Add(TimeSpan.FromSeconds(15)), result.Period);
        }
        
        [Fact]
        public async Task DeserializeTimer_Period_DaprFormat_Monthly()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"period\": \"@monthly\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.FromDays(30), result.Period);
        }
        
        [Fact]
        public async Task DeserializeTimer_Period_DaprFormat_Weekly()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"period\": \"@weekly\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.FromDays(7), result.Period);
        }
        
        [Fact]
        public async Task DeserializeTimer_Period_DaprFormat_Daily()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"period\": \"@daily\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.FromDays(1), result.Period);
        }
        
        [Fact]
        public async Task DeserializeTimer_Period_DaprFormat_Hourly()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"period\": \"@hourly\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.FromHours(1), result.Period);
        }

        [Fact]
        public async Task DeserializeTimer_DueTime_DaprFormat_Hourly()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"dueTime\": \"@hourly\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.FromHours(1), result.DueTime);
            Assert.Equal(TimeSpan.Zero, result.Period);
        }

        [Fact]
        public async Task DeserializeTimer_DueTime_Iso8601Times()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"dueTime\": \"0h0m7s10ms\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Null(result.Ttl);
            Assert.Equal(TimeSpan.Zero, result.Period);
            Assert.Equal(TimeSpan.FromSeconds(7).Add(TimeSpan.FromMilliseconds(10)), result.DueTime);
        }
        
        [Fact]
        public async Task DeserializeTimer_Ttl_DaprFormat_Hourly()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"ttl\": \"@hourly\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.Zero, result.Period);
            Assert.Equal(TimeSpan.FromHours(1), result.Ttl);
        }

        [Fact]
        public async Task DeserializeTimer_Ttl_Iso8601Times()
        {
            const string timerJson = "{\"callback\": \"TimerCallback\", \"ttl\": \"0h0m7s10ms\"}";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(timerJson));
            var result = await ActorManager.DeserializeAsync(stream);
            
            Assert.Equal("TimerCallback", result.Callback);
            Assert.Equal(Array.Empty<byte>(), result.Data);
            Assert.Equal(TimeSpan.Zero, result.DueTime);
            Assert.Equal(TimeSpan.Zero, result.Period);
            Assert.Equal(TimeSpan.FromSeconds(7).Add(TimeSpan.FromMilliseconds(10)), result.Ttl);
        }
        
        private interface ITestActor : IActor { }

        private class TestActor : Actor, ITestActor, IDisposable
        {
            private static int counter;

            public TestActor(ActorHost host) : base(host)
            {
                Sequence = Interlocked.Increment(ref counter);
            }

            // Makes instances easier to tell apart for debugging.
            public int Sequence { get; }

            public bool IsActivated { get; set; }

            public bool IsDeactivated { get; set; }

            public bool IsDisposed { get; set; }

            public void Dispose()
            {
                IsDisposed = true;
            }

            protected override Task OnActivateAsync()
            {
                IsActivated = true;
                return Task.CompletedTask;
            }

            protected override Task OnDeactivateAsync()
            {
                IsDeactivated = true;
                return Task.CompletedTask;
            }
        }

        private class ThrowsDuringOnActivateAsync : Actor, ITestActor
        {
            public ThrowsDuringOnActivateAsync(ActorHost host) : base(host)
            {
            }

            protected override Task OnActivateAsync()
            {
                throw new InvalidTimeZoneException();
            }
        }

        private class ThrowsDuringOnDeactivateAsync : Actor, ITestActor
        {
            public ThrowsDuringOnDeactivateAsync(ActorHost host) : base(host)
            {
            }

            protected override Task OnDeactivateAsync()
            {
                throw new InvalidTimeZoneException();
            }
        }

        private class TestActivator : DefaultActorActivator
        {
            public int CreateCallCount { get; set; }

            public int DeleteCallCount { get; set; }

            public override Task<ActorActivatorState> CreateAsync(ActorHost host)
            {
                CreateCallCount++;;
                return base.CreateAsync(host);
            }

            public override Task DeleteAsync(ActorActivatorState state)
            {
                DeleteCallCount++;
                return base.DeleteAsync(state);
            }
        }
    }
}
