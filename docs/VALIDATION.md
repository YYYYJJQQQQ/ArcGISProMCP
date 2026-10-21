# 验证状态 / Validation status

截至2026-10-21：最近完整本地检查为159项（18 BridgeSmoke+141 MCPIntegration），此前版本有真实Pro3.7、中文属性、格式/坐标系、工作流、保存、异常恢复、布局导出等独立用例记录。自动检查主要使用合成宿主，不能覆盖所有GIS数据或每个Agent。个人验收工程、历史日志/备份不公开，不能由公开源码自动重放所有历史结论。

明确未验收：1.8.3目录显示修补在真实Pro主题/DPI的效果；所有客户端真实运行；任意大型/复杂数据；任意投影/基准面变换；高密标注/所有纸张的美学质量；移动目录后不重建的运行。源码开源适配本身的重新构建/导出/CI结果在发行说明另列，未通过前不声称通过。

普通GitHub Windows CI可编译Server和运行McpIntegration合成检查；不会安装Esri SDK或模拟真实ArcGIS许可。完整BridgeSmoke打包及实际Pro验收需要有授权的本地/自托管环境，不自动使用公共PR运行宿主工作。对不支持的取消、工具栏命令和确认表单准确报告限制。

As of 2026-10-21, the most recent full local baseline has 159 checks: 18 BridgeSmoke plus 141 MCPIntegration. Earlier versions have scoped real-Pro 3.7 evidence for Chinese attributes, conversions/CRS, workflows, saves, interruption handling and layout exports. Synthetic checks are not universal GIS or client validation. Private projects/logs/backups are excluded, so historical acceptance cannot all be replayed from this public tree.

Still unverified: the 1.8.3 directory fix in actual installed Pro themes/DPI; every documented agent; arbitrary large/complex data, datum transformations and dense labels; aesthetic quality across all paper sizes; running after moving the checkout without rebuilding. Public export/rebuild/CI results are listed separately in release notes.

Public Windows CI builds the Server and runs synthetic MCPIntegration checks. It does not install Esri software or emulate licensing. Full package checks and actual Pro validation require a licensed local/self-hosted environment. Never run untrusted pull requests in a privileged GIS host.

首次公开副本测试暴露旧测试依赖开发者的长期保存/切换授权；已经将这两类模拟测试改为独立fixture和共享Server注册，公开默认仍关闭，不用放宽发布配置来通过测试。原失败及修复后的验证在发行说明区分；这不是替使用者授权。

The first public-copy run exposed old tests depending on developer standing save/switch consent. These tests now use isolated fixtures and shared server registration, keeping public defaults closed. Release notes distinguish the failed attempt from subsequent validation; tests never grant permissions for an end user's GIS project.
