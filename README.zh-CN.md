# Unity4DGS — Gaussian Splatting for Unity

[English](README.md) · **中文**

面向 Unity 6+ / URP 的实时 3D/4D 高斯泼溅(Gaussian Splatting)渲染器与程序化动画工具集,支持 visionOS / VR 立体渲染。

[![Unity4DGS 演示视频](https://img.youtube.com/vi/X9RL0WqCAvM/maxresdefault.jpg)](https://youtu.be/X9RL0WqCAvM)

基于 [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)(MIT)构建,在其基础上大幅扩展了 GPU 驱动的动画系统、4D 序列回放、变形(morph)与 XR 渲染路径。

## 功能特性

### 渲染
- GPU 排序的泼溅渲染,采用 radix sort(在无 wave intrinsics 的平台回退到 counting sort)
- 跨模型全局排序:相互重叠的泼溅模型能正确混合与遮挡
- GPU 视锥体 + 不透明度剔除,配合 indirect draw
- 可选的逐渲染器背面剔除,用于从网格表面采样生成的泼溅:泼溅的局部 +Z 视为朝外的表面法线,背对相机的泼溅在接近侧视时淡出
- 可选的 tile-based 渲染路径
- 多种渲染模式:完整泼溅、点云,以及调试视图(点、索引、包围盒、chunk 边界)
- URP Render Feature(主要目标平台);HDRP pass 与内置管线路径也存在,但验证较少

### 动画与特效
- **GaussianPlayer** — 4D 回放:以可配置的帧率播放逐帧泼溅资产序列
- **GaussianAnimator + GaussianAnimVolume** — GPU 程序化动画,通过 box/sphere 体积并带衰减限定作用范围,非破坏性(在临时缓冲区上运行)
- **Modifier 栈** — Dissolve、Wave、Warp、Property、Turbulence、Swirl、Caustic、WheatWave、Converge
- **GaussianModifierStateMachine** — 状态机,支持参数捕获以及 modifier 状态间的混合过渡
- **GaussianMorph** — 两个泼溅资产之间的 GPU 变形过渡
- **GaussianCutout** — box / 椭球裁剪区域
- **GaussianStylizeVolume** — 风格化后处理(立体感知)

![Unity4DGS 演示](Docs/media/demo.gif)

### XR
- visionOS 立体渲染:单次 dispatch 立体渲染、注视点渲染(VRR)支持
- Apple Vision Pro:仅支持 Metal 渲染路径(Compositor Services);不支持 PolySpatial(RealityKit)路径
- Quest 平台理论上可运行,但目前尚未验证

## 环境要求

- Unity **2022.3+** 且启用 **URP**
- 支持 compute shader 的 GPU —— 在 **Vulkan / Metal** 上效果最佳

## 安装

Unity4DGS 是一个 Unity 包(`com.hankun.4dgs`),其清单文件位于 `Assets/4DGS/package.json`。可用以下任一方式添加到已有项目:

**通过 Git URL** —— 在 Package Manager 中选择 *Add package from git URL*,输入(`?path=` 指向包所在的子目录):

```
https://github.com/HankunYu/Unity4DGS.git?path=Assets/4DGS
```

**通过本地磁盘** —— 先 clone 仓库,然后在 Package Manager 中选择 *Add package from disk*,选中 `Assets/4DGS/package.json`:

```
git clone https://github.com/HankunYu/Unity4DGS.git
```

## 快速开始

1. 将 `GaussianSplat URP Feature` 添加到你的 URP Renderer 资产。
2. 在场景中放置一个 `GaussianSplatConfig` 组件(保存全局渲染设置与 shader 引用)。场景里没有时，第 4 步会自动创建。
3. 把 `.ply` / `.spz` 文件放进项目，它会原地导入为泼溅资产；质量、剪枝、splat 预算、SH 阶数等选项在其导入设置（Inspector）里调整，源文件保持不变，可继续给其他工具使用。
4. 把文件拖进 Scene 视图或 Hierarchy，会自动创建一个挂好 `GaussianSplatRenderer` 的 GameObject；训练工具输出的数据 Y 轴朝下，所以它的 Y 缩放为 -1。也可以手动添加组件并指定资产。
5. 对于 4D 序列，把逐帧文件放在一个文件夹里，右键该文件夹选 `Create > Gaussian Splats > Sequence from Folder`（帧按文件名自然顺序排列），再把生成的序列拖进场景，会自动挂上播放它的 `GaussianPlayer`。
6. 想不经训练把模型转成 splat：选中模型（FBX 或预制体），选 `Create > Gaussian Splats > Mesh Splats`。生成的 `.meshsplat` 文件会导入成 splat 资产：对模型下所有网格采样（蒙皮网格取绑定姿态），splat 平贴在表面上。导入设置里可以选点的分布方式（均匀的泊松盘、纯随机、每顶点一个、在折边和开口边缘加密、由虚拟扫描仪打出的 LiDAR 式扫描线，或投影到表面上的规则晶格）和颜色来源（统一色、材质底色 × 贴图、顶点色），也可以调烘焙光照和 splat 形状；模型改动后会自动重新导入。把它拖进场景时会连同模型一起放进来：splat 挂在模型下面，并带一个 `GaussianMeshDriver`，模型播放动画时让 splat 始终贴在表面上（它根据骨骼变换在 GPU 上自己做蒙皮，所以源网格会被隐藏、不需要渲染；不跟随 blend shape）。动画修改器作用在运动中的表面上。把 Output 改成 Sequence，则会把动画片段的一段烘成逐帧的 splat 资产，由 `GaussianPlayer` 播放：可以只采样一次、让点跟着表面走，也可以每帧重新采样，让扫描仪和采样固定不动、模型从中穿过。

## 示例资产

`DemoScene` 引用了一个用 [World Labs](https://www.worldlabs.ai/) **Marble** 生成的高斯泼溅资产("ceramic" 场景)。该资产**未包含在本仓库中** —— 全新 clone 后 `DemoScene` 会出现 missing reference。请自备 `.ply` / `.spz` 并导入(见[快速开始](#快速开始))来填充场景。

## 项目结构

```
Assets/4DGS/
├── Runtime/          # 渲染器、排序、动画系统、modifier、状态机
├── Editor/           # PLY/SPZ 导入器、模型转 splat 导入器、编辑工具
├── Shaders/          # 泼溅 / 点云 shader、排序与 morph compute
├── Resources/        # Compute shader(动画、tile 渲染、工具)
└── Settings/         # URP 管线资产
```

## 许可证

MIT — 见 [LICENSE](LICENSE)。

部分内容版权归 Aras Pranckevičius 所有(c) 2023([UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting))。
