// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Greys a style block's field out in the inspector while the bool field
    // beside it is set: the block then takes the value from elsewhere
    public sealed class GaussianStyleDisabledIfAttribute : PropertyAttribute
    {
        public string Field { get; }

        public GaussianStyleDisabledIfAttribute(string field)
        {
            Field = field;
        }
    }
}
