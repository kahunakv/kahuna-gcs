# Testing

## Run the tests

1. Start the GCS emulator:

   ```sh
   docker compose up -d
   ```

2. Run the tests:

   ```sh
   dotnet test
   ```

If the emulator is not running, the tests that need it are skipped, not failed.

To make sure that the emulator tests ran, do what CI does:

```sh
dotnet test -- xUnit.FailSkips=true
```

With this option, a skipped test fails the run.

## What the emulator supports

We checked each item below against the emulator image.

| Feature | Emulator | How the tests cover it |
| --- | --- | --- |
| Listing with page tokens | Supported | Emulator tests |
| Range reads | Supported | Emulator tests |
| Resumable uploads | Supported | Emulator tests |
| Per-object deletes | Supported | Emulator tests |
| Generation preconditions | Not supported. A create-if-absent write replaces the object. Real GCS returns HTTP 412. | A scripted transport checks the request and the handling of a 412 response. |
| Access control | Not supported. Credentials, IAM, and bucket policy do nothing. | The emulator tests turn off the bucket checks. Separate tests use buckets that report no uniform access. |

## What the tests do not prove

- **ADC, workload identity, IAM, and bucket policy.** No test authenticates. Only a run against a real
  bucket tests these.
- **The 412 response from real GCS.** The tests check the request and the 412 handling. The GCS
  documentation is the only source for the 412 response.
- **Artifacts of many gigabytes.** The tests check the streaming path byte by byte at a few
  megabytes only. A production checkpoint can be much larger.
- **More than one writer in one bucket.** The package assumes one writer, as the local target does.
- **A full backup and restore through `BackupService`.** The tests use each contract member against a
  real storage client. The backup driver, the verifier, the restore engine, and retention are internal
  to `Kahuna.Core`. This package cannot run them. The Kahuna test suite tests them against an object
  store. A test against this store belongs in the Kahuna repository.
