using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Editor
{
    // Where a GameObject or component sits under a renderer, so a look preset
    // can find the same object again under another renderer: the path of
    // names from the renderer down, the component type and its index among
    // the components of that type.
    [Serializable]
    internal struct GaussianLookAddress
    {
        private const string GameObjectType = "GameObject";

        [SerializeField] private string path;
        [SerializeField] private string type;
        [SerializeField] private int index;

        // False when the object is not root's GameObject, a component on it
        // or something below it
        public static bool TryCreate(Transform root, Object obj, out GaussianLookAddress address)
        {
            address = default;
            var component = obj as Component;
            GameObject go = component != null ? component.gameObject : obj as GameObject;
            if (go == null || !go.transform.IsChildOf(root))
                return false;

            string path = "";
            for (Transform t = go.transform; t != root; t = t.parent)
                path = path.Length == 0 ? t.name : t.name + "/" + path;
            address.path = path;

            if (component == null)
            {
                address.type = GameObjectType;
                return true;
            }
            address.type = component.GetType().FullName;
            address.index = Array.IndexOf(go.GetComponents(component.GetType()), component);
            return true;
        }

        public readonly Object Resolve(Transform root)
        {
            Transform t = string.IsNullOrEmpty(path) ? root : root.Find(path);
            if (t == null)
                return null;
            if (type == GameObjectType)
                return t.gameObject;
            int found = 0;
            foreach (Component component in t.GetComponents<Component>())
            {
                if (component == null || component.GetType().FullName != type)
                    continue;
                if (found++ == index)
                    return component;
            }
            return null;
        }

        public override readonly string ToString() => string.IsNullOrEmpty(path) ? type : $"{path} ({type})";
    }
}
