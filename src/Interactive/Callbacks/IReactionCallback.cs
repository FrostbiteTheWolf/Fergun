﻿using System;
using System.Threading.Tasks;
using Discord.Commands;
using Discord.WebSocket;

namespace Fergun.Interactive
{
    public interface IReactionCallback
    {
        ICriterion<SocketReaction> Criterion { get; }

        TimeSpan? Timeout { get; }

        SocketCommandContext Context { get; }

        Task<bool> HandleCallbackAsync(SocketReaction reaction);
    }
}