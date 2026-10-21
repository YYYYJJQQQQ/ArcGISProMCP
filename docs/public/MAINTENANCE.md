# 长期维护 / Long-term maintenance

本项目定位为可长期维护的社区开源 GIS 自动化桥接系统。维护重点：保留已有工具接口；修复可复现的真实GIS问题；任务恢复/幂等/权限安全；性能和制图质量；适配经验证的客户端及未来Pro版本。当前支持基线为Pro/SDK3.7、.NET10、本地Windows，不默认承诺下一版本兼容。

维护采用Issues和Pull Requests，优先处理安全、数据正确性和任务可靠性问题。版本以语义化标签和发行说明交付，预览版明确标记未验证事项；不将编译成功写成真实GIS验收。升级保留配置，破坏性接口变化需在新主版本说明迁移。补丁应尽量小、附适当测试和证据；商业支持或固定时限需另行约定。

维护是尽力而为，不保证固定更新周期、响应时限、永久托管或商业SLA。不存在后台值守服务或自动监控承诺。欢迎更多维护者参与代码审查、双语文档与独立环境验收。若项目未来停止维护，应明确更新此政策及归档状态，而不是继续宣称活跃维护。

This project is intended for long-term community maintenance. Priorities are backward-compatible tools, reproducible real GIS fixes, task recovery/idempotency, permission safety, performance and cartographic quality. The supported baseline is Windows, Pro/SDK 3.7 and .NET 10; future compatibility requires validation.

Use Issues and Pull Requests. Safety, data correctness and reliability take priority. Releases use versioned tags and notes; previews disclose gaps. Builds are not real GIS acceptance. Preserve user configuration during upgrades; breaking interfaces require an explicit migration and major release. Prefer small patches with meaningful tests. Commercial support and deadlines require separate arrangements.

Maintenance is best effort, without a fixed cadence, response deadline, perpetual hosting guarantee or SLA. No unattended monitoring service is promised. Additional maintainers, bilingual documentation and independent host verification are welcome. If maintenance ends, update this policy and archive status honestly.
