﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FizzWare.NBuilder.Extensions;
using Raven.Abstractions.Data;
using Raven.Client.Document;
using Raven.Client.Exceptions;
using Raven.Json.Linq;
using Raven.Tests.Common;
using Xunit;

namespace Raven.Tests.Conflicts
{
    public class ConflictDocumentResolverTest : ReplicationBase
    {
        public void InitializeForConflict(out DocumentStore store2)
        {
            using (var store1 = CreateStore())
            {
                store2 = CreateStore();
                store1.DatabaseCommands.Put("users/1", null, new RavenJObject
                {
                    {"Name", "Ayende"}
                }, new RavenJObject());

                store2.DatabaseCommands.Put("users/1", null, new RavenJObject
                {
                    {"Name", "Rahien"}
                }, new RavenJObject());

                var list = new BlockingCollection<ReplicationConflictNotification>();
                var taskObservable = store2.Changes();
                taskObservable.Task.Wait();
                var observableWithTask = taskObservable.ForAllReplicationConflicts();
                observableWithTask.Task.Wait();
                observableWithTask
                    .Subscribe(list.Add);

                TellFirstInstanceToReplicateToSecondInstance();

                ReplicationConflictNotification replicationConflictNotification;
                Assert.True(list.TryTake(out replicationConflictNotification, TimeSpan.FromSeconds(10)));
                Etag conflictedEtag = null;

                try
                {
                    store2.DatabaseCommands.Get("users/1");
                }
                catch (ConflictException ex)
                {
                    conflictedEtag = ex.Etag;
                }

                Assert.Equal(conflictedEtag, replicationConflictNotification.Etag);
            }
        }

        [Fact]
        public void ConflictResolutionWithLoadResolveToRemote()
        {
            DocumentStore store2;
            InitializeForConflict(out store2);

            store2.DatabaseCommands.Put(Constants.RavenReplicationConfig, null, new RavenJObject
            {
                {"DocumentConflictResolution", "ResolveToRemote"},
                {"AttachmentConflictResolution", "ResolveToRemote"}
            }, new RavenJObject());

            using (var session = store2.OpenSession())
            {
                var user = session.Load<dynamic>("users/1");
                Assert.Equal(user.Name, "Ayende");
            }
            store2.Dispose();
        }

        [Fact]
        public void ConflictResolutionWithGetResolveToRemote()
        {
            DocumentStore store2;
            InitializeForConflict(out store2);

            store2.DatabaseCommands.Put(Constants.RavenReplicationConfig, null, new RavenJObject
            {
                {"DocumentConflictResolution", "ResolveToRemote"},
                {"AttachmentConflictResolution", "ResolveToRemote"}
            }, new RavenJObject());

            var user = store2.DatabaseCommands.Get("users/1");
            RavenJToken value;
            user.DataAsJson.TryGetValue("Name", out value);
            Assert.Equal(value.Value<string>(), "Ayende");
            store2.Dispose();
        }




        [Fact]
        public void ConflictResolutionWithLoadResolveToLocal()
        {
            DocumentStore store2;
            InitializeForConflict(out store2);

            store2.DatabaseCommands.Put(Constants.RavenReplicationConfig, null, new RavenJObject
            {
                {"DocumentConflictResolution", "ResolveToLocal"},
                {"AttachmentConflictResolution", "ResolveToLocal"}
            }, new RavenJObject());

            using (var session = store2.OpenSession())
            {
                var user = session.Load<dynamic>("users/1");
                Assert.Equal(user.Name, "Rahien");
            }
            store2.Dispose();
        }

        [Fact]
        public void ConflictResolutionWithGetResolveToLocal()
        {
            DocumentStore store2;
            InitializeForConflict(out store2);

            store2.DatabaseCommands.Put(Constants.RavenReplicationConfig, null, new RavenJObject
            {
                {"DocumentConflictResolution", "ResolveToLocal"},
                {"AttachmentConflictResolution", "ResolveToLocal"}
            }, new RavenJObject());

            var user = store2.DatabaseCommands.Get("users/1");
            RavenJToken value;
            user.DataAsJson.TryGetValue("Name", out value);
            Assert.Equal(value.Value<string>(), "Rahien");
            store2.Dispose();
        }

        [Fact]
        public void ConflictResolutionWithGetResolveToLatest()
        {
            DocumentStore store2;
            InitializeForConflict(out store2);

            store2.DatabaseCommands.Put(Constants.RavenReplicationConfig, null, new RavenJObject
            {
                {"DocumentConflictResolution", "ResolveToLatest"},
                {"AttachmentConflictResolution", "ResolveToLatest"}
            }, new RavenJObject());

            var user = store2.DatabaseCommands.Get("users/1");
            RavenJToken value;
            user.DataAsJson.TryGetValue("Name", out value);
            Assert.Equal(value.Value<string>(), "Rahien");
            store2.Dispose();
        }

        [Fact]
        public void ConflictResolutionWithLoadResolveToLatest()
        {
            DocumentStore store2;
            InitializeForConflict(out store2);

            store2.DatabaseCommands.Put(Constants.RavenReplicationConfig, null, new RavenJObject
            {
                {"DocumentConflictResolution", "ResolveToLatest"},
                {"AttachmentConflictResolution", "ResolveToLatest"}
            }, new RavenJObject());

            using (var session = store2.OpenSession())
            {
                var user = session.Load<dynamic>("users/1");
                Assert.Equal(user.Name, "Rahien");
            }
            store2.Dispose();
        }
    }


}
