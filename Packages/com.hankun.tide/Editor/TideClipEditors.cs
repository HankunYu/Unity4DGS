using System;
using System.Collections.Generic;
using UnityEditor;

namespace Tide.Editor
{
    /// <summary>Finds the <see cref="TideClipEditor"/> for a clip type.</summary>
    internal static class TideClipEditors
    {
        private static Dictionary<Type, TideClipEditor> s_byClipType;

        public static TideClipEditor For(Type clipType)
        {
            if (s_byClipType == null)
            {
                s_byClipType = new Dictionary<Type, TideClipEditor>();
                foreach (Type type in TypeCache.GetTypesDerivedFrom<TideClipEditor>())
                {
                    if (type.IsAbstract || type.IsGenericType || type.GetConstructor(Type.EmptyTypes) == null)
                        continue;
                    var editor = (TideClipEditor)Activator.CreateInstance(type);
                    if (editor.ClipType != null)
                        s_byClipType[editor.ClipType] = editor;
                }
            }
            for (Type t = clipType; t != null; t = t.BaseType)
            {
                if (s_byClipType.TryGetValue(t, out TideClipEditor editor))
                    return editor;
            }
            return null;
        }
    }
}
