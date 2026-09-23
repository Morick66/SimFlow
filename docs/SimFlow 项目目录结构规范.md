# SimFlow 项目目录结构规范

**文档状态：正式约定**
**适用范围：SimFlow 项目目录的创建、使用、迁移与归档**
**最后更新：2026-09-23（识别 Delivery 中唯一的 Word 报告；改名后仍可打开）**

## 1. 为什么需要这份规范

SimFlow 的程序行为只依赖项目目录里的**两个文件**：`project.json`（必需）和 `cover.*`（可选）。其余目录结构没有任何代码依赖，但它决定了三件事：人能不能一眼看懂、迁移/归档会不会丢东西、以及项目之间能不能直接对比。本文把结构固定下来，避免"文件夹建了但没人知道是干什么的"。

## 2. 标准结构

```
<Work 根>\SIM_20260917_001\        ← 项目目录（= ProjectCode，Work 根的直接子目录）
│
├─ project.json                    ← 项目档案｜SimFlow 维护
├─ cover.png                       ← 可选封面｜由界面设置
│
├─ Documents\                      ← 与版本无关的文档
│
├─ Versions\                       ← 项目级版本
│  ├─ V001\                        ← 一个版本 = 一套自包含内容
│  │  ├─ version.json              ← 版本档案｜SimFlow 维护
│  │  ├─ 3D_Model\                 ← 三维 / CAD
│  │  ├─ CAE_Model\                ← 求解器模型
│  │  ├─ Data\                     ← 该版本输入数据
│  │  └─ Results\                  ← 该版本仿真结果
│  └─ V002\                        ← 与 V001 完全同构
│
└─ Delivery\                       ← 项目级对外交付物
    ├─ 仿真分析报告.docx
    ├─ Figures\                    ← 报告用图表
    └─ Animation\                  ← 演示动画
```

**目录内不放占位文件**（没有 `README.txt`、`.keep` 之类）。空目录靠程序在迁移/复制时重建目录结构保留，见 §6。

归档到工作站后，由 SimFlow 迁移自动生成为 `<Archive 根>\yyyy\MM\<项目目录>\`，内容与上述结构一致。

## 3. 目录职责

| 目录 | 放什么 |
| --- | --- |
| 项目根 | `project.json`、封面 |
| `Documents\` | 需求单、图纸、零件清单、参考资料、评审记录等**与版本无关的输入与依据** |
| `Versions\Vxxx\3D_Model\` | SolidWorks 装配/零件、`.x_t`、`.STEP` 等三维模型 |
| `Versions\Vxxx\CAE_Model\` | Adams `.bin/.adm/.cmd`、ANSYS Mechanical / Maxwell 工程、柔性体 `.mnf/.mtx/.bdf` |
| `Versions\Vxxx\Data\` | 该版本的输入数据：参数表、实测数据、`csv` / `xlsx` |
| `Versions\Vxxx\Results\` | 该版本的**原始仿真产出**：求解器输出、结果数据、过程图表 |
| `Delivery\` | **对外交付物**：正式报告；图表放 `Figures\`，动画放 `Animation\` |

一句话：**过程中的东西进版本目录，对外交付的进 `Delivery`。**

SimFlow 的“仿真报告”操作遵循这条规则：`Delivery` 顶层有且仅有一个 Word 报告时直接打开，因此报告改名后仍可识别；没有报告时把设置中的模板复制为 `Delivery\模板文件名`；多个报告时提示冲突，不猜测也不覆盖。

## 4. 硬性规则

严重度：🔴 = 会导致导入/迁移出错；🟡 = 会造成混乱或数据丢失；⚪ = 整洁性。

### 4.1 位置与根目录

> 「依据」列只给文件与方法名，不写行号——行号会随每次改动失效。

| # | 规则 | 依据 |
| --- | --- | --- |
| 🔴 R1 | 项目目录必须是 Work 根的**直接子目录**，中间不能加分组层（如 `Work\2026\SIM_x`）。 | 扫描器把**目录叶子名**当 `RelativePath`（`ProjectScannerService.ScanAsync`），路径解析是 `根 + RelativePath`（`StorageLocationService.ResolveProjectPath`）。嵌套一层 ⇒ 导入后路径指向不存在的目录。 |
| 🔴 R2 | Archive 根下必须是 `yyyy\MM\项目目录` 三层，不能把项目直接放在 Archive 根下。 | 归档路径由 `ArchivedAt` 拼成 `根\yyyy\MM\RelativePath`（`StorageLocationService.ResolveProjectPath`），迁移时也这样生成（`ProjectMigrationService.MigrateAsync`）。 |
| 🔴 R3 | 三个根目录（本机 Work / 工作站 Work / 工作站 Archive）**不能互相嵌套**。 | 扫描遍历每个根；同一份 `project.json` 被两个根找到时，`MetadataMatches` 要求存储位置一致（`ProjectScannerService.MetadataMatches`），必然报冲突。 |
| 🟡 R4 | 同一根内目录名唯一（Windows 不区分大小写）。 | `ProjectCode` 列 `COLLATE NOCASE` 唯一（`ProjectRepository.GetByCodeAsync` 与建表语句）。 |

### 4.2 结构与内容

| # | 规则 | 依据 |
| --- | --- | --- |
| 🔴 R5 | 项目根必须有 `project.json`，`id`、`name` 非空。 | 扫描唯一入口（`ProjectScannerService.ScanAsync`）。 |
| 🔴 R6 | 标准目录（`Documents`、`Versions\Vxxx\{3D_Model,CAE_Model,Data,Results}`、`Delivery\{Figures,Animation}`）**保持存在**，不要手工删除或改名。 | 创建时建一次，之后程序不会补建；空目录能被带到目标位置是靠迁移/复制时重建目录结构（`ProjectMigrationService.RunTransferAsync`、`DirectoryCopy.Copy`）。 |
| 🔴 R7 | 任何层级都不允许符号链接、目录连接（junction）。 | 迁移遇 ReparsePoint **直接抛异常中止**（`ProjectMigrationService.EnumerateSafeFiles` / `EnumerateSafeDirectories`）；换 Work 根的复制则是**静默跳过**（`DirectoryCopy.Copy`）⇒ 要么失败要么悄悄丢数据。 |
| 🔴 R8 | **版本目录必须自包含**：`Versions\V002\` 不能靠链接复用 `V001` 的文件，只能物理复制。 | 同 R7；并且 V001/V002 之间要能在断开彼此的情况下单独迁移。 |
| 🔴 R9 | 项目内除根目录那一个，**任何位置都不能再有名为 `project.json` 的文件**。 | 扫描是**全递归**枚举 `project.json`（`ProjectScannerService.ScanAsync`）⇒ 子目录里的一份会被当成"另一个项目"，路径按叶子目录算错。 |
| 🟡 R10 | 每个版本都保持 `3D_Model / CAE_Model / Data / Results` **四件套齐全**，即使某个版本没改 3D 模型也保留该目录。 | 约定：版本之间才能直接对比，缺目录会让"哪一版有结果"变成靠记忆。 |
| 🟡 R11 | 不要在标准分类目录之外再加"按人 / 按日期 / 按阶段"的分组层；建议最深 4 层（`Versions\Vxxx\分类\文件`）。 | 约定：迁移逐文件 SHA-256，越深越多成本；Windows 路径长度上限 260 字符。 |

### 4.3 维护与禁止

| # | 规则 | 依据 |
| --- | --- | --- |
| 🔴 R12 | 不要在项目根放自己命名的 `cover.` 前缀文件。 | 设置/清除封面会删除项目根**所有** `cover.*`（`ProjectService.SetCoverImageAsync` / `ClearCoverImageAsync`）。 |
| 🔴 R13 | 不要手工创建 `Versions\Vxxx` 目录。 | 版本号来自数据库 `MAX+1`（`ProjectService.CreateVersionAsync`），`CreateDirectory` 幂等，随后会**覆盖**该目录里的 `version.json`。要留档就先在界面建版本、再放文件。 |
| 🔴 R14 | Work 根里若出现 `.<项目名>.<32位ID>.partial` 目录，必须用「迁移恢复」继续或放弃。 | 暂存目录建在目标根下且**内含完整副本（含 `project.json`）**（`ProjectMigrationService.GetStagingPath`）⇒ 残留会被扫描当成第二个项目并报冲突。 |
| 🟡 R15 | `project.json` 与 `version.json` 由 SimFlow 维护，不要手工编辑。 | 启动时按数据库内容原子重写档案（`ProjectService.RetryPendingMetadataAsync`、`ProjectMetadataStore.WriteAtomicAsync`）。 |
| ⚪ R16 | 目录里不需要放任何占位文件；要写说明就写在项目根的自定义文档里，不要依赖程序生成的说明文件。 | 程序不生成也不读取任何目录说明文件。 |

### 4.4 命名

| # | 规则 |
| --- | --- |
| 🟡 R17 | 路径避免 `\ / : * ? " < > \|`，不以空格或点结尾，不使用保留名（`CON` `PRN` `AUX` `NUL` `COM1`…）。 |
| 🟡 R18 | 固定用这套名字与大小写：`Documents` `Versions` `3D_Model` `CAE_Model` `Data` `Results` `Delivery` `Figures` `Animation`。 |
| ⚪ R19 | 文件名统一 UTF-8，避免出现乱码名。 |

## 5. 版本规则

| 项 | 规则 |
| --- | --- |
| 版本粒度 | **项目级**：一个项目一套 `Versions\Vxxx`，一个版本 = 一套完整的模型/数据/结果 |
| 版本号 | `V001`、`V002`…（由 SimFlow 生成，不要手工编号，见 R13） |
| 版本内容 | 四件套 `3D_Model / CAE_Model / Data / Results`，保持齐全（R10） |
| 版本间关系 | **完全独立、各自自包含**（R8）。V002 需要 V001 的模型时必须复制，不能用链接 |
| 与工具级迭代的关系 | 求解器内部的 `V1/V2/…`（如 `CAE_Model\V1`）是**工具级**迭代，属于该版本内部的细节，与 `Versions\Vxxx` 不是一回事 |
| 版本记录 | `version.json` 写标题、改动说明、状态；界面"版本"页读的是数据库，不扫目录 |

## 6. 空目录怎么保留

迁移/归档与"更换本机 Work 路径"的复制，都**只枚举文件**、只为文件的父目录建目录。而 `Documents\`、`Delivery\Figures\`、`Delivery\Animation\` 以及新版本刚建出来的四个子目录一开始都是空的，所以必须先按源结构重建目录：

| 路径 | 做法 |
| --- | --- |
| 迁移 / 归档 | `ProjectMigrationService.RunTransferAsync` 在复制前用 `EnumerateSafeDirectories` 枚举源目录，并在暂存目录里逐个重建，然后才复制文件 |
| 更换本机 Work 路径 | `DirectoryCopy.Copy` 同样先 `EnumerateDirectories` 重建目录再复制文件 |

因此**不需要在任何目录里放占位文件**；反过来说，程序只在"创建项目/新建版本"时建这些目录，手工删掉之后不会被补建（R6）。

## 7. 实现位置

| 行为 | 代码 |
| --- | --- |
| 新建项目时创建整个结构 | `ProjectService.CreateProjectDirectories` |
| 新建版本时创建四件套 | `ProjectService.CreateVersionDirectories` |
| 交付物目录 | `ProjectService.CreateDeliveryDirectories` |
| 迁移时保留空目录 | `ProjectMigrationService.RunTransferAsync` + `EnumerateSafeDirectories` |
| 换 Work 根复制时保留空目录 | `DirectoryCopy.Copy` |
| 创建版本与路径解析 | `ProjectService.CreateVersionAsync`、`StorageLocationService.ResolveProjectPath` |

自动化测试：`CreateProject_WritesDatabaseAndPortableMetadata`（结构与"不写占位文件"）、`CreateVersion_ProducesStandardVersionDirectories`（新建版本四件套）、`Migration_VerifiesThenWaitsForExplicitCleanup`（空目录随迁移保留）。

## 8. 新建项目自检清单

- [ ] 项目目录 = `SIM_yyyyMMdd_NNN`，在 Work 根**直接子目录**下且唯一
- [ ] 根目录有 `project.json`（`id`/`name` 非空），没有多余的说明/占位文件
- [ ] `Documents\`、`Delivery\{Figures,Animation}\`、`Versions\V001\{3D_Model,CAE_Model,Data,Results}\` 都存在
- [ ] 全项目没有任何符号链接 / 目录连接
- [ ] 除根目录外没有名为 `project.json` 的文件
- [ ] 根目录没有自己命名的 `cover.` 前缀文件
- [ ] 没有手工创建的 `Versions\Vxxx` 目录
- [ ] 目录名大小写与 R18 一致

## 9. 已知的后续改进（未实施）

1. 扫描器可跳过 `.` 开头的目录并收敛扫描范围（Work 根只查一级、Archive 只查 `yyyy\MM` 两层），使 R1/R2/R9/R14 由代码强制保证。
2. 帮助页（设置 → 帮助）是静态 XAML，与本文档及 `CreateProjectDirectories` 构成三处结构文本，尚未做单一数据源。
