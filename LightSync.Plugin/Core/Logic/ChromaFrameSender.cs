using System;
using System.Threading.Tasks;

namespace Ambilight.Logic
{
    /// <summary>Serializes SDK writes and observes failures before the caller reuses its color grid.</summary>
    internal static class ChromaFrameSender
    {
        private static readonly object Gate = new object();

        public static void Send(Func<Task> send)
        {
            lock (Gate)
            {
                // Invoke on a worker so a manual UI kick cannot deadlock on a captured UI context.
                // Wait for completion: at most one SDK write is in flight, with no stale-frame backlog.
                Task.Run(send).GetAwaiter().GetResult();
            }
        }
    }
}
