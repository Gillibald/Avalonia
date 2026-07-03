using System;
using System.Threading;
using Avalonia.Platform;

namespace Avalonia.Imaging.TestKit.Contract
{
    /// <summary>
    /// Temporarily binds a backend (and optionally a render interface) for code paths
    /// that resolve them through the locator. The locator is process-global, so entries
    /// are serialized across parallel test collections; the gate is a semaphore so a
    /// scope opened before an await can be released from another thread.
    /// </summary>
    internal static class LocatorScope
    {
        private static readonly SemaphoreSlim s_gate = new(1, 1);

        public static IDisposable With(IImagingBackend backend) => WithCore(backend, null);

        public static IDisposable With(IImagingBackend backend, IPlatformRenderInterface renderInterface) =>
            WithCore(backend, renderInterface ?? throw new ArgumentNullException(nameof(renderInterface)));

        private static IDisposable WithCore(IImagingBackend backend, IPlatformRenderInterface? renderInterface)
        {
            s_gate.Wait();

            try
            {
                var scope = AvaloniaLocator.EnterScope();

                try
                {
                    if (renderInterface is not null)
                    {
                        AvaloniaLocator.CurrentMutable
                            .Bind<IPlatformRenderInterface>()
                            .ToConstant(renderInterface);
                    }

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
                s_gate.Release();
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
                s_gate.Release();
            }
        }
    }
}
