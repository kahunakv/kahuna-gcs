# Glossary

| Term | Meaning |
| --- | --- |
| Kahuna | A distributed key-value store. See [kahunakv/kahuna](https://github.com/kahunakv/kahuna). |
| GCS | Google Cloud Storage. A Google service that stores files ("objects") in containers ("buckets"). |
| Bucket | A named GCS container for objects. |
| Object | One file in a bucket. Each object has a name, also called a key. |
| Prefix | A text that starts the name of each object that Kahuna writes, for example `prod/`. |
| Host | The process that runs Kahuna: the Kahuna server, or an application that embeds `Kahuna.Core`. |
| Backup target | The place where Kahuna writes backups. The default target is a local directory. This package adds the `gcs` target. |
| Provider | `GcsBackupStorageProvider`. Kahuna finds it by its target name, `gcs`, and uses it to open the backup storage of a node. |
| Extension assembly | A DLL that the Kahuna server loads with `--extension-assembly`. This package is one. |
| PITR | Point-in-time recovery. A restore of the data as it was at a selected time. |
| Full backup | A complete copy of the data at one time. |
| Incremental backup | The changes after the last backup. |
| Checkpoint | The set of files that a full backup writes. |
| WAL segment | A part of the write-ahead log (WAL). The WAL records each change. An incremental backup is a set of WAL segments, one for each partition. |
| Artifact | A file that a backup writes: a checkpoint file or a WAL segment. |
| Manifest | A file that lists all artifacts of one backup and the digest (hash) of each artifact. A backup exists only when its manifest exists. |
| Scratch directory | A local directory where Kahuna puts backup files before an upload and after a download. |
| Retention | The Kahuna task that deletes old backups. |
| Garbage collection | The Kahuna task that deletes artifacts that no backup uses. |
| Orphan sweep | The part of garbage collection that deletes artifacts that no manifest lists. |
| ADC | Application Default Credentials. The Google method that finds credentials from the environment. |
| Workload identity | A GKE feature that gives a pod a Google identity without a key file. |
| GKE, GCE | Google Kubernetes Engine and Google Compute Engine. |
| IAM | Identity and Access Management. The Google system that controls which identity can do which operation. |
| Resumable upload | A GCS upload protocol that sends an object in chunks. |
| Emulator | A local program that acts like GCS. The tests use it. |
