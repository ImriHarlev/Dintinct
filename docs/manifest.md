# Job Manifest

Each job produces a manifest file named `{jobId}_manifest.json` (e.g. `3fa85f64_manifest.json`). It describes the full decomposition of a package — what files it contained, how each file was converted, and how each converted file was split into chunks.

Network B uses the manifest as the authoritative blueprint to reassemble the original package once all chunks arrive.

---

## Generation (Network A)

The manifest is built in three steps inside the `DecompositionWorkflow`:

1. **`HeavyProcessingActivities.DecomposeAndSplitAsync`** — reads the source package, applies conversion rules per file type, splits each converted file into numbered chunks, and returns a `DecompositionMetadata` object that captures the full `Files → ConvertedFiles → Chunks` hierarchy.

2. **Workflow enrichment** — the workflow uses a `with` expression to attach request-level fields (`AnswerType`, `TargetNetwork`, `CallingSystemId`, etc.) to the metadata.

3. **`ManifestActivities.WriteManifestAsync`** — serializes the enriched metadata as camelCase indented JSON and writes it to the manifest outbox directory:
   ```
   {ManifestOutboxPath}/{jobId}_manifest.json
   ```

---

## Structure

```json
{
  "jobId": "string",
  "packageType": "string",          // e.g. "zip"
  "originalPackageName": "string",
  "sourcePath": "string",
  "targetPath": "string",
  "targetNetwork": "string",
  "callingSystemId": "string",
  "callingSystemName": "string",
  "externalId": "string",
  "totalChunks": 0,                 // total chunks across all files
  "answerType": "string",           // e.g. "RabbitMQ"
  "answerLocation": "string|null",
  "files": [
    {
      "originalRelativePath": "string",
      "originalFormat": "string",
      "appliedConversion": ["string"],       // list of conversion names applied
      "convertedFiles": [
        {
          "convertedRelativePath": "string",
          "chunks": [
            {
              "name": "string",    // e.g. "3fa85f64_chunk_1.bin"
              "index": 0,          // 1-based order for reassembly
              "checksum": "string"
            }
          ]
        }
      ]
    }
  ]
}
```

---

## Reassembly (Network B)

When the manifest file arrives over RabbitMQ, `ProxyEventConsumer` detects the `_manifest.json` suffix and sends a `ManifestArrived` signal to the `AssemblyWorkflow`.

The workflow then:

1. **Parses** — `ParseAndPersistManifestActivities.ParseAndPersistManifestAsync` reads the file, deserializes it, and persists an `AssemblyBlueprint` to MongoDB (`Status = "Aggregating"`). The blueprint captures `TotalChunks` as the completion target.

2. **Waits** — the workflow waits until received + failed + unsupported chunks ≥ `TotalChunks`.

3. **Assembles** — `AssembleAndValidate` uses the `Files → ConvertedFiles → Chunks` tree from the blueprint to:
   - Sort chunks by `ChunkDescriptor.Index`
   - Concatenate the raw bytes in order
   - Reverse each conversion listed in `AppliedConversion`
   - Re-pack everything into the original package format (`PackageType`)

4. **Reports & dispatches** — writes a CSV report, dispatches the answer, and notifies the calling system.

---

## Example

**Input package:** `images/` directory containing `photos/sunset.jpg`

The JPEG is converted to PNG and split into 3 chunks. The resulting manifest:

```json
{
  "jobId": "3fa85f64",
  "packageType": "directory",
  "originalPackageName": "images",
  "sourcePath": "/network-a/inbox/images",
  "targetPath": "/network-b/output/images",
  "targetNetwork": "NetworkB",
  "callingSystemId": "sys-01",
  "callingSystemName": "ImagingSystem",
  "externalId": "ext-99",
  "totalChunks": 3,
  "answerType": "RabbitMQ",
  "answerLocation": null,
  "files": [
    {
      "originalRelativePath": "photos/sunset.jpg",
      "originalFormat": "jpeg",
      "appliedConversion": ["JPEG_TO_PNG"],
      "convertedFiles": [
        {
          "convertedRelativePath": "photos/sunset.png",
          "chunks": [
            { "name": "3fa85f64_chunk_1.bin", "index": 1, "checksum": "a1b2c3" },
            { "name": "3fa85f64_chunk_2.bin", "index": 2, "checksum": "d4e5f6" },
            { "name": "3fa85f64_chunk_3.bin", "index": 3, "checksum": "g7h8i9" }
          ]
        }
      ]
    }
  ]
}
```

**Reassembly:** Network B receives all three chunks, concatenates `chunk_1.bin` + `chunk_2.bin` + `chunk_3.bin` in index order, reverses the `JPEG_TO_PNG` conversion to recover `sunset.jpg`, and writes it back into the output directory structure.
