# Contributor guidance / 协作约定

Read README and docs/VALIDATION.md first. Keep SDK access inside QueuedTask.Run; only DTOs leave MCT. Preserve MCP names/parameters/protocol v1. Never self-authorize, overwrite raw GIS, replay uncertain writes or replace a running add-in/toolbox. Build/test per INSTALL, record limits, and inspect rendered maps.

先读README与验证状态；沿用线程、权限和接口边界，测试使用独立工程。缺少能力时复用可用客户端工具，不绕过拒绝。所有生成文件保存在本项目artifacts内。
