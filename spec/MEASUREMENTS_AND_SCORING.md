# 测量、异常与推荐合同
## 区分三类数字
`native_http_delay`是内核自身URLTest口径，是否包括握手取决于版本与unified-delay；`cold_http_ttfb`是本软件从发起请求到首字节；都不是ICMP ping或模型推理首token。
来源含source_instance/epoch、node_revision、target_id/revision、network_context、methodology；不同方法/URL/地址族/网络环境/过期时间不混排。根history目标不明时写target_id=native_unknown，不从当前默认URL倒推过去。

## 网络可达与业务可用分离
无Key `https://api.openai.com/v1/models` 返回401可记HTTP到达且业务unverified，不需要API Key。ChatGPT网页403记录blocked_or_challenged，不直接算节点网络故障，更不能低延迟=最好OpenAI节点。429/5xx与连接失败分栏；目标策略可将其列入受限状态但保留原始事实。
不自动调用收费模型接口。首版不声称测得token/s或实际Codex生成时间。

## 推荐算法 v1（产品方案，非上游现有能力）
每个目标至少10个可评价新样本，24小时窗口，至少覆盖两个采样轮次；显示样本数与采样跨度。
A=HTTP网络成功次数/可评价尝试次数；用户取消、配置失败、采集器关闭、资源取消不算失败。
成功耗时均值m，标准差sd（总体方差，稳定求和/Welford）。
L=clamp((3000-m)/2800,0,1)；V=clamp(1-sd/1000,0,1)。
S_target=100*(0.50*A+0.35*L+0.15*V)。无成功样本S=0，不给中性稳定性分。参数属于版本化默认值，不当行业标准。
AI工作：OpenAI API 60%，ChatGPT 30%，GitHub 10%，用户可调整。先各自算分，再按共同目标集加权；不能把所有URL混成一个latency列表。缺少必需目标时score=null/覆盖不足，不替该节点单独重分配权重。
关键目标近窗有效网络失败比例≥20%：总分上限20；关键目标成功均值>2500ms：总分上限40。先算分再cap，解释列显示触发条件。ChatGPT持续403不是网络超时，但AI业务状态为受限/未知，不能发绿色“业务可用”推荐。
IP稳定性与异常时长首版独立显示，不将缺失IP补成100分。用户选择IP profile时必须所有候选拥有同覆盖的观察数据；暂不做自动切节点。

## 异常事件
按node×target×method×context分组，连续2个新失败确认异常，连续2个新成功确认恢复。记录last_success、first_failure、last_failure、first_recovery、confirmation时刻；展示采样估计和上下界，不伪称精确断网秒数。
采样间隔Δ：末次成功到首个失败之间是起点区间；末失败到首个恢复成功之间是恢复区间。睡眠、计划暂停、源失联、无活跃连接分开。控制API失联只证明采集源不可用，不能证明所有节点断网。

## IP观察
主动请求IP echo、检查响应IPv4/IPv6格式，按节点revision/服务/地址族/context分组。比较连续有效样本；空档>3倍计划周期则结束比较段。统计unique_ip_count、comparable_pairs、observed_changes、covered_seconds；变化/小时分母只能是可比较段总秒数。
两个echo服务结果不能互相串接算跳变；echo出口也不能保证OpenAI使用同样出口。节点名字含Fixed不构成固定IP证明。
