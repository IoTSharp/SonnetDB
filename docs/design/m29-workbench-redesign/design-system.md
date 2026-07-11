# M29 视觉与组件系统

## 1. 设计原则

1. **任务优先**：默认首屏只呈现当前任务，不在一个页面同时平铺浏览、治理、监控和诊断。
2. **稳定几何**：主题、对象和模型切换不改变全局轨道、顶部栏、Explorer 和工作区页签的位置。
3. **渐进披露**：详情进入检查器，历史和结果进入抽屉，危险动作进入审批层。
4. **密而不小**：容量依靠明确分区、滚动和可调整分隔条获得，不依靠 10px 字体或 22px 控件。
5. **跨模型一致、模型内专用**：外壳、审批、历史和结果共用；浏览器、查询器和检查器尊重模型差异。

## 2. 基础尺寸

| Token | 值 | 用途 |
| --- | ---: | --- |
| `rail-width` | 64px | 全局模块轨 |
| `topbar-height` | 56px | 命令、连接、通知和账户 |
| `explorer-width` | 304px | 默认资源浏览器，可调 240-420px |
| `workspace-tabs-height` | 44px | 已打开对象与查询 |
| `inspector-width` | 384px | 默认检查器，可调 320-520px |
| `result-height` | 280px | 默认底部结果抽屉，可调 180-55vh |
| `control-height` | 36px | 默认输入和按钮 |
| `compact-control-height` | 32px | 表格工具栏最小值 |
| `tree-row-height` | 40px | Explorer 对象行 |
| `table-row-height` | 42px | 默认数据行 |
| `radius` | 6px | 对话框、输入和真正的卡片上限 |

## 3. 排版

- 界面字体：`Segoe UI Variable`, `Microsoft YaHei UI`, sans-serif。
- 代码与标识符：`Cascadia Code`, `SFMono-Regular`, monospace。
- 工作台标题 20px/28px，区域标题 15px/22px，正文 14px/20px，辅助信息 12px/18px。
- 字重只使用 400、600、700；避免用字重和颜色同时制造过多层级。
- 所有 letter-spacing 为 0。

## 4. 浅色主题

| 语义 | 色值 |
| --- | --- |
| App canvas | `#F5F8FC` |
| Main surface | `#FFFFFF` |
| Subtle surface | `#F7F9FC` |
| Raised surface | `#FFFFFF` |
| Primary text | `#17212B` |
| Secondary text | `#596574` |
| Muted text | `#7A8696` |
| Divider | `#DCE3EC` |
| Strong divider | `#C8D1DD` |
| Interactive | `#0F6CBD` |
| Interactive hover | `#115EA3` |
| Studio dark action | `#061F2C` |
| Healthy | `#107C10` |
| Warning | `#F7630C` |
| Danger | `#C50F1F` |
| Focus ring | `#62ABF5` |

低饱和冰蓝、薄荷、蜜桃和淡紫只允许出现在外层 canvas 的轻微环境色中；表格单元格、编辑器、检查器和审批层保持中性高对比。

## 5. 深色主题

深色主题采用深色全局 chrome 与中性工作面，不使用整页蓝黑单色：

- Rail/Topbar：`#07151B` / `#0B2028`。
- Explorer/Main：`#111A20` / `#151F26`。
- Raised：`#1C2830`。
- Divider：`#30404B`。
- Primary text：`#F4F7F9`。
- Interactive：`#32B8B0`。

深浅主题必须共用所有布局 token 和状态语义。

## 6. 数据密度

- 表头固定，42px 数据行；紧凑模式 36px，但仅由用户显式启用。
- 数字右对齐，时间、offset、ID 和代码使用等宽字体。
- 长文本只在表格中截断；完整内容进入右侧检查器。
- 标签只表达离散状态或类型，不把每个字段都包装为 pill。
- 默认一个主操作，其余动作进入邻近次要按钮或更多菜单。

## 7. 状态

- Loading：保留原布局骨架，禁止用居中 spinner 造成页面跳动。
- Empty：说明当前范围并给出一个直接动作，例如“创建索引”或“调整过滤器”。
- Error：显示发生在哪个模型/连接/请求，保留用户输入和重试入口。
- Partial failure：Explorer 某一模型失败时只在该分组行提示，不阻断其他模型。
- Read-only：在对象标题附近显示锁图标与原因，隐藏或禁用写动作但不隐藏数据。
- Offline：顶部连接状态变为警告，工作台保留最后一次结果并标明时间。

## 8. 响应式

- `>= 1440px`：Explorer + 主工作区 + Inspector 三栏。
- `1100-1439px`：Inspector 覆盖式打开，Explorer 保持可见。
- `800-1099px`：Explorer 抽屉化；工作区页签仍保留。
- `< 800px`：仅支持巡检和只读浏览；表格与检查器互斥，编辑/批量治理引导至桌面。

## 9. 可访问性

- 键盘焦点始终可见；焦点环不依赖背景色。
- 文字与背景至少达到 WCAG AA；状态不只依赖颜色。
- 图标按钮提供 `aria-label` 和 tooltip，最小点击区域 32px。
- 分隔条支持键盘微调；Escape 关闭最上层抽屉或对话框。
- 表格选中行同时使用背景、左侧标记和 `aria-selected`。
