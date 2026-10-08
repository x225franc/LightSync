using System;

namespace Ambilight.Logic
{
    // Only retry ownership after resume/unlock. Ordinary priority changes must not
    // cause a rediscovery loop that competes with another foreground application.
    internal sealed class LampArrayRecovery
    {
        private TimeSpan _until;
        private TimeSpan _unavailableSince;
        private bool _wasUnavailable;

        public void Begin(TimeSpan now)
        {
            _until = now + TimeSpan.FromMinutes(3);
            _unavailableSince = now;
            _wasUnavailable = true;
        }

        public bool ShouldReopen(TimeSpan now, bool available)
        {
            if (available)
            {
                _wasUnavailable = false;
                return false;
            }
            if (!_wasUnavailable)
            {
                _unavailableSince = now;
                _wasUnavailable = true;
            }
            if (now >= _until || now - _unavailableSince < TimeSpan.FromSeconds(45))
                return false;
            _unavailableSince = now;
            return true;
        }
    }
}
