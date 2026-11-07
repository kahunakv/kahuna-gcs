# Bucket requirements

Kahuna never creates the bucket. The bucket location, storage class, and access policy are your
decisions. A bucket that Kahuna made without them would have no correct values.

## Settings that Kahuna checks at startup

Kahuna does not start if one of these settings is off:

| Setting | Why it is necessary |
| --- | --- |
| Uniform bucket-level access | Without it, object ACLs (per-object access lists) can give access that the bucket policy does not show. |
| Public access prevention, set to "enforced" | Backup artifacts are a full copy of the committed cluster data. Without this setting, one IAM change can make the backups public. |

These settings give the same protection as the local target. The local target makes a manifest
readable by its owner only, before it writes the manifest. A manifest names each artifact and its
digest, so no other user must read it, not even for a short time.

To turn off a check, set `RequireUniformBucketLevelAccess` or `RequirePublicAccessPrevention` to
`false`. Do this only when you accept the risk.

## Settings that stop deletes from freeing storage

These bucket settings keep a deleted object:

- Object versioning
- Soft delete
- A retention policy

With one of them, retention and garbage collection delete objects without error, but the storage is
not freed. You still pay for the deleted objects, and they can be recovered.

By default, Kahuna logs a warning and starts. Kahuna does not refuse the bucket, because GCS turns on
soft delete for each new bucket by default.

To change this, set `DeletionPolicyReaction`:

| Value | Use it when |
| --- | --- |
| `Warn` | The default. |
| `Fail` | Storage cost or a legal obligation to erase data needs deletes to free storage. |
| `Ignore` | You checked the bucket behaviour in a different way. |
