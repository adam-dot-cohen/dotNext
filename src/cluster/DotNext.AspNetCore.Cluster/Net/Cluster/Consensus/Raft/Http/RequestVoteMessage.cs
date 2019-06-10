﻿using System.Net;
using Microsoft.AspNetCore.Http;

namespace DotNext.Net.Cluster.Consensus.Raft.Http
{
    internal sealed class RequestVoteMessage : RaftHttpBooleanMessage
    {
        internal const string MessageType = "RequestVote";

        internal RequestVoteMessage(IPEndPoint sender)
            : base(MessageType, sender)
        {
        }

        internal RequestVoteMessage(HttpRequest request)
            : base(request)
        {
        }
    }
}
