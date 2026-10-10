# Parity Run mq-a7619df1

Started: 2026-10-10T08:20:06.6980814+00:00

| Scenario | sonnetdb | nats | Diff |
|---|---|---|---|
| publish_consume_ack | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| consumer_group_offset | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| replay_after_restart | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| fan_out_10p_10c | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| backpressure_unbounded_producer | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | nats | SonnetDB gap |
|---|---|---|---|---|
| publish_consume_ack | Mq, MqConsumerGroup | pass | pass |  |
| consumer_group_offset | Mq, MqConsumerGroup | pass | pass |  |
| replay_after_restart | Mq, MqReplayFromOffset | pass | pass |  |
| fan_out_10p_10c | Mq, MqConsumerGroup | pass | pass |  |
| backpressure_unbounded_producer | Mq, MqReplayFromOffset | pass | pass |  |
