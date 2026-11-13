# Third-party notices

This library implements the Azure Artifacts Universal Packages download protocol. It is not affiliated with or endorsed
by Microsoft, and contains no Microsoft code. Its implementation was informed by the following MIT-licensed projects.

## azure-devops-rust-api

The download flow (package metadata, `dedup/urls`, manifests, dedup nodes, and decompression of chunks) is modelled on
the `artifacts_download` module of Microsoft's Azure DevOps Rust API:
<https://github.com/microsoft/azure-devops-rust-api> (`azure_devops_rust_api/src/artifacts_download/`).
License: [MIT](https://github.com/microsoft/azure-devops-rust-api/blob/main/LICENSE).

## BuildXL

The dedup node format and content hashing (chunk and node identifiers) follow Microsoft's BuildXL content store
hashing: <https://github.com/microsoft/BuildXL> (`Public/Src/Cache/ContentStore/Hashing`), MIT License,
Copyright (c) Microsoft Corporation.

## MS-XCA

Chunk compression is the "Plain LZ77" algorithm of Microsoft's published
[MS-XCA](https://learn.microsoft.com/openspecs/windows_protocols/ms-xca/) specification (Xpress Compression Algorithm).
