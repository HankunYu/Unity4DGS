// SPDX-License-Identifier: MIT
// Asset format description shared by the splat decoders.

#ifndef GAUSSIAN_SPLAT_FORMAT_HLSL
#define GAUSSIAN_SPLAT_FORMAT_HLSL

// Match GaussianSplatAsset.SHFormat. Values above NORM6 are the cluster
// formats: a per-splat table index in the other data, Float16 table entries.
#define SPLAT_SH_FMT_32F 0
#define SPLAT_SH_FMT_16 1
#define SPLAT_SH_FMT_11 2
#define SPLAT_SH_FMT_6 3

// Packed formats (GaussianSplatAsset.FormatFlags): bits 0-7 position,
// 8-15 scale, 16-23 SH format, 24-25 SH degree
uint SplatFormatPos(uint flags) { return flags & 0xFF; }
uint SplatFormatScale(uint flags) { return (flags >> 8) & 0xFF; }
uint SplatFormatSH(uint flags) { return (flags >> 16) & 0xFF; }
uint SplatFormatSHDegree(uint flags) { return (flags >> 24) & 0x3; }

bool SplatSHClustered(uint shFormat) { return shFormat > SPLAT_SH_FMT_6; }

// SH coefficients per color channel excluding the base color: 0, 3, 8, 15
uint SplatSHCoeffCount(uint degree) { return (degree + 1) * (degree + 1) - 1; }

// Bytes per SH entry; must match GaussianSplatAsset.GetSHStride
uint SplatSHStride(uint shFormat, uint degree)
{
    uint coeffs = SplatSHCoeffCount(degree);
    if (shFormat == SPLAT_SH_FMT_32F)
        return coeffs * 12;
    if (shFormat == SPLAT_SH_FMT_11)
        return coeffs * 4;
    if (shFormat == SPLAT_SH_FMT_6)
        return (coeffs * 2 + 3) & ~3;
    return (coeffs * 6 + 3) & ~3;
}

#endif // GAUSSIAN_SPLAT_FORMAT_HLSL
