# Bucket layout

Kahuna writes these objects. `{prefix}` is the `Prefix` option. `{backupId:N}` is the backup GUID,
written as 32 hexadecimal digits without hyphens.

```
{prefix}{backupId:N}.manifest          the manifest
{prefix}{backupId:N}/checkpoint/…      the checkpoint of a full backup
{prefix}{backupId:N}/partition_N.wal   the WAL segments of an incremental backup
```

- A backup exists only when its manifest exists. Kahuna writes the manifest last.
- Checkpoint files keep their relative paths exactly.
- Manifests and artifacts use the same prefix, as they use the same directory on the local target.
  Kahuna tells them apart by their names.

## More than one cluster in one bucket

Give each cluster a different prefix. A cluster does not see the objects under the prefix of a
different cluster.
