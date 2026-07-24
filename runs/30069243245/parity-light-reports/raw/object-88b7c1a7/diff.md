# Parity Run object-88b7c1a7

Started: 2026-07-24T05:26:16.5837119+00:00

| Scenario | sonnetdb | minio | Diff |
|---|---|---|---|
| putget_1gb_object | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| multipart_upload_5gb | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| range_read_offsets | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| list_objects_v2_pagination | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |
| copy_delete_presigned_url_lifecycle | ✅ pass (rows=1) | ✅ pass (rows=1) | ✅ within tolerance |

## Capability gaps

| Scenario | Required | sonnetdb | minio | SonnetDB gap |
|---|---|---|---|---|
| putget_1gb_object | Object | pass | pass |  |
| multipart_upload_5gb | Object, ObjectMultipart | pass | pass |  |
| range_read_offsets | Object | pass | pass |  |
| list_objects_v2_pagination | Object | pass | pass |  |
| copy_delete_presigned_url_lifecycle | Object | pass | pass |  |
