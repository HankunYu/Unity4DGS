// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Which renderer the objects in a hierarchy belong to. Volumes, cutouts
    // and particle emitters belong to the nearest GaussianSplatRenderer above
    // them, so renderers can nest (a person's particle emitters as its
    // children) without the outer one taking what belongs to the inner one.
    public static class GaussianHierarchy
    {
        private static class Buffer<T>
        {
            public static readonly List<T> Items = new();
        }

        // The active components of type T on the owner's GameObject and below
        // it, leaving out the subtree of every nested renderer
        public static void GetOwnedComponents<T>(Component owner, List<T> results)
        {
            results.Clear();
            Collect(owner.transform, results, true);
        }

        // The renderer the component belongs to: on its own GameObject or the
        // nearest above it
        public static GaussianSplatRenderer OwningRenderer(Component component) =>
            component.GetComponentInParent<GaussianSplatRenderer>(true);

        // The nearest renderer above the component's GameObject, not counting
        // one on the GameObject itself
        public static GaussianSplatRenderer ParentRenderer(Component component)
        {
            Transform parent = component.transform.parent;
            return parent != null ? parent.GetComponentInParent<GaussianSplatRenderer>(true) : null;
        }

        private static void Collect<T>(Transform t, List<T> results, bool isRoot)
        {
            if (!t.gameObject.activeInHierarchy)
                return;
            if (!isRoot && t.TryGetComponent(out GaussianSplatRenderer _))
                return;
            t.GetComponents(Buffer<T>.Items);
            results.AddRange(Buffer<T>.Items);
            for (int i = 0; i < t.childCount; i++)
                Collect(t.GetChild(i), results, false);
        }
    }
}
