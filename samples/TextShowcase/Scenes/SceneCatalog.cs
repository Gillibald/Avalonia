using System.Collections.Generic;

namespace TextShowcase.Scenes
{
    /// <summary>The scenes in talk order.</summary>
    internal static class SceneCatalog
    {
        public static IReadOnlyList<Scene> Create() => new Scene[]
        {
            new SideBySideScene(),
            new ReferenceScene(),
        };
    }
}
