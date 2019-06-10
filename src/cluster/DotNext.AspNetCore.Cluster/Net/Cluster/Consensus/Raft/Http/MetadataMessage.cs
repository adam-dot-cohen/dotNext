﻿using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace DotNext.Net.Cluster.Consensus.Raft.Http
{
    internal sealed class MetadataMessage : RaftHttpMessage<MemberMetadata>
    {
        internal const string MessageType = "Metadata";

        internal MetadataMessage(IPEndPoint sender)
            : base(MessageType, sender)
        {
        }

        internal static async Task<MemberMetadata> GetResponse(HttpResponseMessage response)
        {
            var serializer = new DataContractJsonSerializer(typeof(MemberMetadata));
            return (MemberMetadata)serializer.ReadObject(await response.Content.ReadAsStreamAsync()
                .ConfigureAwait(false));
        }

        internal static Task CreateResponse(HttpResponse response, IPEndPoint identity, MemberMetadata metadata)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            FillResponse(response, identity);
            var serializer = new DataContractJsonSerializer(typeof(MemberMetadata));
            serializer.WriteObject(response.Body, metadata);
            return Task.CompletedTask;
        }
    }
}
