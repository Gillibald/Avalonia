using System;
using System.Threading;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Temporarily binds a backend as the application's imaging backend for code paths
    /// that resolve it through the locator. The locator is process-global, so entries
    /// are serialized across parallel test collections.
    /// </summary>
    internal static class LocatorScope
    {
        private static readonly object s_gate = new();

        public static IDisposable With(IImagingBackend backend)
        {
            Monitor.Enter(s_gate);

            try
            {
                var scope = AvaloniaLocator.EnterScope();

                try
                {
                    ImagingBackend.Register(backend);
                    return new Releaser(scope);
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }
            }
            catch
            {
                Monitor.Exit(s_gate);
                throw;
            }
        }

        private sealed class Releaser : IDisposable
        {
            private IDisposable? _scope;

            public Releaser(IDisposable scope) => _scope = scope;

            public void Dispose()
            {
                var scope = Interlocked.Exchange(ref _scope, null);

                if (scope is null)
                    return;

                scope.Dispose();
                Monitor.Exit(s_gate);
            }
        }
    }
}
