# 贡献 / Contributing

提交Issue前请去掉个人路径、账号、令牌、原始数据和工程内容，提供组件/Pro/SDK/客户端版本、最小复现、期望与实际行为、脱敏错误码/任务终态。不要上传完整日志目录或真实APRX。敏感问题见SECURITY.md。

修改遵守现有分层和MCT线程约束；不要重做已有工具或扩宽权限。保留工具名/参数/结构和协议v1，避免将不确定写入自动重跑。构建命令见INSTALL；普通CI仅测合成宿主，GIS修改仍须用独立工程做对应Pro实测，制图须看全图和局部。没有许可/数据时标注未验证，不制造结果。

PR应解释具体问题、行为变化、验证和限制，同步更新中英文说明。不得引入凭据、生成缓存、EsriDLL、原始GIS数据或其他人的授权配置。代码以MIT提交，第三方材料必须保留其许可。

Before filing an issue, remove personal paths, tokens, account details, source GIS data and project contents. Include component/Pro/SDK/client versions, a minimal reproducer, expected/observed behavior and sanitized error/terminal status. Do not upload complete logs or real APRX files. See SECURITY.md for sensitive reports.

Preserve architecture, MCT rules, tool interfaces and protocol v1. Do not broaden permissions or retry uncertain writes. See INSTALL for builds. CI uses synthetic hosts; GIS changes require scoped real-Pro testing on independent copies, and maps require visual review. Clearly state unverified work.

PRs should explain the concrete problem, resulting behavior, validation and limitations, and update bilingual documentation. Never commit credentials, caches, Esri binaries, raw GIS data or another user's consent. Contributions use MIT; third-party materials retain their own terms.
