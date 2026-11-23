# AutoCAD 适配器真机验证协议（工作环境执行）

> 状态：开发机无 AutoCAD（孤儿 ProgID 注册，CLSID {363E5B47-885D-44C3-89EB-A2AB2129B57E}
> 无 LocalServer32）。AcadRuntime 已实现但 **CompatibleUnverified**。
> 本协议在工作环境（实装 AutoCAD 2024-2026）执行，产出 Verified 升级证据。

> **更正注记（2026-11-23，追加式，上文旧状态保留存档）**：本机已实装 AutoCAD 2014
> （R19.1.18.0.0，chs profile）+ 天正电气 5.0。注册态已核实有效：
> HKCR\AutoCAD.Application → CLSID {BD0DEB94-63DB-4392-9420-6EEE05094B1F} →
> LocalServer32 = `C:\Program Files\Autodesk\AutoCAD 2014\acad.exe /Automation`
> （64 位视图，acad.exe PE Machine=0x8664）——头部"孤儿 ProgID 无 LocalServer32"描述已过时。
> 但真机验证 **BLOCKED**：HKCU\Software\Microsoft\Windows NT\CurrentVersion\
> AppCompatFlags\Layers 存在 `acad.exe : RUNASADMIN` 兼容层（用户/安装器设置），
> 强制 acad.exe 提升启动；非提升测试宿主 COM 激活即抛 0x800702E4
> （ERROR_ELEVATION_REQUIRED），未启动任何进程。acad.exe 内嵌 manifest 为 asInvoker，
> CLSID 无 Elevation 键——RUNASADMIN 兼容层是唯一提升源。
> 解除需用户决策（取消 acad.exe"以管理员身份运行"兼容设置，或授权提升运行自动化会话；
> 擅自移除可能破坏用户许可/破解环境，不在验证代理权限内）。
> 本次执行证据：outputs/autocad-verify-20261123/（3 真机测试显式跳过，矩阵不升级，
> Blocks.Add 签名序仍未核定）。

## 前置约束（CUIX 竞态根因 b，PR#17 事故教训）

1. 验证期间**禁止**并行运行任何 AutoCAD 自动化或人工会话——并发实例共享用户
   profile 会竞态损坏 acad.cuix，下次启动弹模态修复框挂起自动化。
2. 测试已串行化（xunit.runner.json + [Collection("AcadLive")]）；不得改回并行。
3. 验证前备份用户 profile CUIX（%APPDATA%\Autodesk\AutoCAD *\*\*\Support\acad.cuix），
   记录 SHA256；验证后复算比对（金丝雀测试自动做 zip CRC 校验）。

## 步骤

1. 确认安装：`AutoCAD.Application` ProgID 的 CLSID 必须有 LocalServer32
   （PowerShell：`Get-ChildItem 'Registry::HKEY_CLASSES_ROOT\CLSID\{...}'`）。
2. 运行条件真机测试：
   `dotnet test tests/CadAgent.Adapters.Acad.Tests --filter Category=RequiresAutoCAD`
   预期：4 测试全绿（不再跳过）。记录 AutoCAD 版本号（GetInfo 输出）。
3. 运行全量回归：`dotnet test CadAgent.sln`（含 ZWCAD 真机，若工作环境也有中望）。
4. **Blocks.Add 签名序探针核定**：DrawingOps_LiveVendorSession_ReadBack 的
   EnsureBlock 若 COM 报错（参数序不符），翻 `VendorProfiles.Acad.BlocksAddNameFirst`
   为 false 重跑；核定结果（文档序成立/相反）写入矩阵 testCoverage 与 spec §12。
   ZWCAD 前车之鉴：实测序=(origin, Name) 与 AutoCAD 文档序相反——不赌文档，真机核定。
5. SaveAs 格式确认：读回闸口已断言 DWG 可读；另用 AutoCAD 人工打开 ops.dwg
   确认无修复提示、实体完整（保真锚=厂商运行时，spec §3）。
6. CUIX 金丝雀：UserProfileCuix_ZipIntact 绿 + 备份哈希比对一致。
7. 证据归档：测试输出、版本号、探针核定结论、CUIX 哈希前后比对 →
   `outputs/autocad-verify-<date>/`（含 MANIFEST.txt）。

## 矩阵升级（须全部满足）

- 步骤 2-6 全绿且证据归档。
- `src/CadAgent.Cli/vendor-support.json` AutoCAD 条目：
  status → "Verified"，verifiedAt → 执行日期，versionRange → 实测版本，
  testCoverage → 实测覆盖描述（含探针核定结论）。
- spec §12 追加验证注记。
- 未实测版本仍保持 CompatibleUnverified（打开时警示，决策权归人）。

## 已知语义注记

- GetInfo 在守卫超时（已有 acad 进程）时报 Available=false → CLI 降级 DXF+警示。
  这是安全侧选择：忙≠不可用，但降级不丢数据；如需区分，后续切片细化 RuntimeInfo。
- accoreconsole（真无头）暴露面更小（无 CUIX 加载），本切片 deferred；
  COM 路径与 ZWCAD 同构，守卫机制已参数化可直接复用。
