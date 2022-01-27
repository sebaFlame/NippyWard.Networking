using System;

namespace ThePlague.Networking.Connections
{
    public interface IConnectionTerminalFeature
    {
        public void Complete(Exception ex);
    }
}