# 架构 / Architecture

```text
Agent / MCP client
  -> official MCP SDK stdio server (.NET 10)
  -> current-user Windows named pipe, protocol v1
  -> ArcGIS Pro Add-in
  -> QueuedTask.Run / Esri SDK
     or MCT input resolution -> GPThread -> ArcPy toolbox
```

Contracts：无Esri依赖的DTO/校验；Transport：实例发现/消息限额/命名管道；Server：工具注册、确认、编排、历史；Add-in/Services：工程/图层/布局与线程内操作；python：隔离的版本运行时。源码清单包括全部生产模块，旧Python运行时保留作兼容参考，不随意替换正在运行的工具箱。

任务有操作编号、持久回执、终态查询及显式取消。取消是协作式，可能在取消请求后成功；中断/失败/未知不同。恢复只使用已证明成功的结果，未知写入禁止自动重投。没有跨SDK事务或任意副作用回滚。

授权配置由人维护。结果写入新路径，APRX保存需对应授权、目标校验和备份。公共默认关闭长期保存/切换与自动清理；公开包不继承作者的目录授权。

Contracts contains SDK-free DTOs/validation; Transport handles discovery, limits and pipes; Server registers tools and coordinates consent/workflows/history; Add-in/Services owns SDK operations; Python runtimes are versioned. All production modules are included. Older Python runtimes are retained for compatibility; never replace an in-use toolbox.

Operations have IDs, durable receipts, terminal queries and explicit cooperative cancellation. A cancellation request can still finish successfully. Interrupted, failed and unknown are distinct. Resume reuses only proven outputs; uncertain writes are not automatically replayed. There is no cross-SDK transaction or general rollback.

Humans own grants. Outputs use new paths; APRX saves require authorization, validation and backups. Public defaults disable standing save/switch consent and automatic cleanup and never inherit the author's workspace grants.

ServerApplication复用官方注册和启动构建，生产入口仍只按用户配置读取授权；合成测试以DI注入独立授权，不提供生产CLI授权覆盖。测试启动器只接受ArcGISProMCP.Test.*管道及根内独立fixture，不能直接连实际Pro管道。

ServerApplication shares official registration/startup construction. Production consent still comes from user-owned configuration; synthetic tests inject an isolated policy through DI, without adding a production CLI consent override. The test launcher accepts only ArcGISProMCP.Test.* pipes and dedicated fixture roots, not real Pro pipes.
