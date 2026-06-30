# M6 入口点与 PlayerLoop 调度 · 考题

## 🟢 概念题

1. `IInitializable.Initialize` 与 `IStartable.Start` 在执行时机上有何本质不同？
2. `IPlayerLoopItem.MoveNext` 返回 `false` 代表什么？
3. 入口点的调度是谁、在什么时候触发的？
4. 默认的入口点异常处理器是什么？
5. `EntryPointDispatcher` 的生命周期是什么（Singleton/Scoped/Transient）？

<details><summary>参考答案要点</summary>

1. `Initialize` 在 `Dispatch()` 当场立即同步执行；`Start` 被包成 LoopItem 挂入 PlayerLoop 的 Startup 阶段，在下一个该阶段帧执行一次。
2. 该 LoopItem 执行完毕、应从 PlayerLoopRunner 的 FreeList 中移除（一次性项执行后/被 Dispose 后）。
3. `EntryPointsBuilder.EnsureDispatcherRegistered` 注册的构建回调，在容器 `Build` 完成（EmitCallbacks）时 `Resolve<EntryPointDispatcher>().Dispatch()`。
4. `UnityEngine.Debug.LogException`（在 `EnsureDispatcherRegistered` 里默认注册）。
5. Scoped（每个作用域一个，调度本作用域的入口点）。
</details>

## 🟡 机制题

1. 为什么入口点用 `ContainerLocal<IReadOnlyList<T>>` 解析而非 `IReadOnlyList<T>`？
2. Tickable 的 `MoveNext` 返回 `!disposed`，Startable 返回 `false`，这如何统一处理一次性与持续性？
3. `PlayerLoopHelper.EnsureInitialized` 如何保证只初始化一次？
4. 一个 tickable 被 Dispose 后，它是如何从 PlayerLoopRunner 中被移除的？
5. 当某入口点的 handler 为 null 时，Tickable 系和 Startable 系分别如何处理异常？
6. `Dispatcher` 持有的 `CompositeDisposable` 在 Dispose 时做了什么？对 LoopItem 有何效果？

<details><summary>参考答案要点</summary>

1. 让每个作用域只解析"本作用域 + 父可见"的入口点集合（`localScopeOnly`），避免子作用域重复执行父的入口点。裸 `IReadOnlyList<T>` 会沿父链聚合导致重复。
2. `PlayerLoopRunner.Run` 不关心类型，只看 `MoveNext` 返回值：true 留下、false `RemoveAt`。一次性项执行后返回 false 自我移除，持续项返回 true 保留。
3. `Interlocked.CompareExchange(ref initialized, 1, 0) != 0` 即已初始化则直接返回，保证进程级 once。
4. `Dispose` 置 `disposed=true`；下一帧 `Run` 调其 `MoveNext` 返回 false（`if (disposed) return false`）→ `RemoveAt(i)` 原位 null 化出列。
5. 都重新 `throw`（`if (exceptionHandler == null) throw;`）。但实际默认有 `Debug.LogException` handler，所以默认是记录并继续。
6. 弹栈逐个 `Dispose` 所有登记的 LoopItem（置各自 `disposed`），它们在下一帧 `MoveNext` 返回 false 自动出列。
</details>

## 🔴 架构陷阱题

1. **同底座对比**：Init 系（立即执行）和 Tick 系（挂载执行）都来自同一个 Dispatch，但若一个对象同时实现 `IInitializable` 和 `ITickable`，它的两个方法分别何时被调用？会被解析两次成两个实例吗？
2. 如果把 tickable 的注销改成"Dispose 里直接 `FreeList.RemoveAt`"而不是置标志，可能在什么场景出错？
3. 子作用域和父作用域都注册了实现 `IStartable` 的对象，若入口点用裸 `IEnumerable<IStartable>` 解析，会发生什么可观察的 bug？
4. `MoveNext` 内捕获异常后 `Publish` 而非 `throw`，这保证了什么？如果某个 tickable 每帧抛异常，会怎样？
5. 入口点接口（IStartable 等）方法体里若调用 `container.Resolve<T>()` 解析一个尚未构造的 Scoped 服务，安全吗？
6. PlayerLoop 注入是进程级全局 once，但 Dispatcher 是每作用域一个。两次进入 Play 模式（Editor 不重启域）时，PlayerLoop 阶段会被重复插入吗？（推断，需在 Unity 环境验证）
7. Startable 的 LoopItem 执行一次后出列，但 Dispatcher 仍持有它的引用（disposable.Add），这算泄漏吗？

<details><summary>参考答案要点</summary>

1. `Initialize()` 在 Dispatch 当场立即调用；`Tick()` 每帧调用。是否两个实例取决于注册——若用 `Register<T>().AsImplementedInterfaces()` 单注册，则两个集合里是**同一个实例**（单例/作用域生命周期下 Resolve 命中同一缓存），不会变两个对象。
2. 与正在进行的 `Run` 遍历竞态：`Run` 用固定 `span.Length` 按 index 遍历，若 Dispose 在别处线程/重入中直接 RemoveAt，可能让 `Run` 访问到刚被 null 的槽或打乱预期。置标志 + 下帧出列把删除收敛到 `Run` 自身，单点修改安全。
3. 父的 IStartable 会被子作用域的 Dispatcher 再次拉取并 `Start()` 一次，造成重复启动（重复初始化、重复订阅等）。ContainerLocal 隔离正是为防此。
4. 保证单个入口点异常不会中断同一帧其他入口点的执行、也不会让整个 PlayerLoop 崩溃。每帧抛异常的 tickable 会每帧 `Publish`（默认每帧 LogException），刷屏但不致命。
5. 取决于该 Scoped 服务能否在当前作用域解析；若已注册且不构成环，安全（容器按需 Lazy 构造）。但在 Initialize 阶段做重解析要注意不要触发循环或依赖尚未 Dispatch 的副作用——属于使用约定而非框架强保证。
6. 推断：`EnsureInitialized` 的 `initialized` 是静态字段，Editor 不重启域时跨 Play 会话保持为 1，故不重复插入；但若启用"Domain Reload"则会重置。**此点未在本仓库运行验证**。
7. 不算严重泄漏：它生命周期与 Dispatcher（Scoped）一致，作用域 Dispose 时 `disposable.Dispose()` 一并释放。只是出列后到作用域销毁前会被多持有一会儿，属可接受。
</details>

## ✍️ 实操题

1. 用 Facade 写测试：一个对象同时实现 `IInitializable`+`ITickable`，断言 Initialize 恰好被调用 1 次、Tick 随帧数递增被调用 N 次。
2. 给 Facade 增加 `LateTickable`（持续，另一阶段），并用两个 `LoopRunner` 验证"Update 阶段先于 LateUpdate 阶段"的相对执行顺序。
3. 复现"容错不中断"：注册两个 tickable，第一个每帧抛异常，断言第二个仍每帧被执行，且异常被 handler 捕获计数。

<details><summary>参考答案要点</summary>

1. 用计数器对象；`Dispatch` 后立即断言 init==1；循环 `update.Tick()` N 次后断言 tick==N。注意 startup 的 Start 也应只 1 次。
2. 新增 `LateTickableLoopItem` + 第三个 runner；主循环顺序 `update.Tick(); late.Tick();`，用时间戳列表断言 update 项先于 late 项。
3. handler 记录异常计数；循环 Tick 后断言"好 tickable 计数==帧数"且"异常计数==帧数"，证明坏 tickable 不阻断好 tickable。
</details>
