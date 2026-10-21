# 验证状态 / Validation status

## 1.9.0安装预览 / Installer preview

2026-10-21本地完整打包回归169项通过：18 BridgeSmoke +151 MCPIntegration，其中新增10项安装/运行根安全检查。真实Setup.exe在两个项目内不同中文/空格目录解包成功；安装后的Server.exe通过官方MCP客户端与独立合成命名管道完成initialize、78工具、health_check、关闭自主保存及日志根验证。重复目标安装返回失败且原配置哈希不变；portable DLL不含开发根元数据，包内无Esri DLL/PDB/个人授权。窗口预览实际渲染查看，第一次DPI布局有问题并已修正。

未执行：官方注册后的Add-in1.9.0实际Pro加载、真实Python/地图操作、其他机器/无开发环境安装、不同Windows主题/DPI。未替换当前插件、自动保存或修改原始GIS数据。Setup文件准备与Server合成桥接检查不等于完整宿主验收。当前保留preview标签。

Local packaging/regression passed 169 checks (18 BridgeSmoke +151 synthetic integration checks, including 10 installer safety checks). The actual Setup.exe extracted into two distinct Chinese/space-containing directories; installed Server.exe passed official MCP initialize, 78-tool listing, health, closed save consent and installation-local logs through an isolated synthetic pipe. Existing targets were refused unchanged. Portable assemblies omit developer-root metadata; no Esri DLL/PDB/personal grants are bundled. The rendered installer was inspected and its initial DPI issue fixed.

Not yet verified: official registration/loading of add-in1.9.0 in actual Pro, real Python/map operations for this release, installation on another/no-development computer and all Windows DPI/themes. Current Pro was not replaced and no source GIS/APRX was changed. This remains a preview, not a claim of universal installed-host acceptance.

## 历史基线 / Previous baseline

截至2026-10-21：最近完整本地检查为159项（18 BridgeSmoke+141 MCPIntegration），此前版本有真实Pro3.7、中文属性、格式/坐标系、工作流、保存、异常恢复、布局导出等独立用例记录。自动检查主要使用合成宿主，不能覆盖所有GIS数据或每个Agent。个人验收工程、历史日志/备份不公开，不能由公开源码自动重放所有历史结论。

明确未验收：1.8.3目录显示修补在真实Pro主题/DPI的效果；所有客户端真实运行；任意大型/复杂数据；任意投影/基准面变换；高密标注/所有纸张的美学质量；1.9.0前旧构建移动目录后不重建的运行。源码开源适配本身的重新构建/导出/CI结果在发行说明另列，未通过前不声称通过。

普通GitHub Windows CI可编译Server和运行McpIntegration合成检查；不会安装Esri SDK或模拟真实ArcGIS许可。完整BridgeSmoke打包及实际Pro验收需要有授权的本地/自托管环境，不自动使用公共PR运行宿主工作。对不支持的取消、工具栏命令和确认表单准确报告限制。

As of 2026-10-21, the most recent full local baseline has 159 checks: 18 BridgeSmoke plus 141 MCPIntegration. Earlier versions have scoped real-Pro 3.7 evidence for Chinese attributes, conversions/CRS, workflows, saves, interruption handling and layout exports. Synthetic checks are not universal GIS or client validation. Private projects/logs/backups are excluded, so historical acceptance cannot all be replayed from this public tree.

Still unverified: the 1.8.3 directory fix in actual installed Pro themes/DPI; every documented agent; arbitrary large/complex data, datum transformations and dense labels; aesthetic quality across all paper sizes; running after moving the checkout without rebuilding. Public export/rebuild/CI results are listed separately in release notes.

Public Windows CI builds the Server and runs synthetic MCPIntegration checks. It does not install Esri software or emulate licensing. Full package checks and actual Pro validation require a licensed local/self-hosted environment. Never run untrusted pull requests in a privileged GIS host.

首次公开副本测试暴露旧测试依赖开发者的长期保存/切换授权；已经将这两类模拟测试改为独立fixture和共享Server注册，公开默认仍关闭，不用放宽发布配置来通过测试。原失败及修复后的验证在发行说明区分；这不是替使用者授权。

The first public-copy run exposed old tests depending on developer standing save/switch consent. These tests now use isolated fixtures and shared server registration, keeping public defaults closed. Release notes distinguish the failed attempt from subsequent validation; tests never grant permissions for an end user's GIS project.

托管CI还暴露了旧测试700ms预算过紧；放宽有界测试预算后发现取消测试原先会把短超时当作收到取消。现改为发送明确notifications/cancelled并核对模拟后台在deadline前取消。仅停止客户端等待不等于服务端/真实GP已停止；实际长任务仍用cancel_analysis_job/cancel_layout_job/cancel_workflow并查询终态。修补不提高生产超时、不虚构即时取消。

Hosted CI exposed an overly tight 700ms fixture budget. Increasing the bounded test allowance revealed that the old cancellation check could pass due to the short deadline. It now sends notifications/cancelled explicitly and observes fixture cancellation before the deadline. Ending a client's wait is not proof that server/GP work stopped. Real background work still uses explicit cancellation tools and terminal queries; production timeout/cancellation claims are not broadened.
