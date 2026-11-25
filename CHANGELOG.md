# Changelog

## [0.14.0](https://github.com/arminzou/integrios/compare/v0.13.0...v0.14.0) (2026-11-25)


### ⚠ BREAKING CHANGES

* containers now run as uid 1654. Before upgrading a Compose deployment, hand the Admin key-ring volume to that user once, and make the mounted Tenant secret directories readable by it; deploy/README.md has the commands. Without the chown, Admin exits at startup with an unwritable key ring.

### Bug Fixes

* **admin:** hold a lapsed session while a key create is in flight ([ace08c2](https://github.com/arminzou/integrios/commit/ace08c2fab08b14dd77ca6e4075fbadc7076c979))
* **admin:** hold a sheet open only for writes whose answer arrives once ([ec1e46f](https://github.com/arminzou/integrios/commit/ec1e46fbb989967350d114dc661bdeb4c36f6ea4))
* **admin:** hold sign-out while a new key is on screen ([5fb0746](https://github.com/arminzou/integrios/commit/5fb07461b3d4ca42aba0be2a8fd355d2c65d1ed7))
* **admin:** keep a cancelled session read from replacing the sign-in token ([7cfdb3b](https://github.com/arminzou/integrios/commit/7cfdb3bc067afac2b8d85049b3bbfd31ed47a16e))
* **admin:** keep a new key's reveal open until the Operator closes it ([7ba0e73](https://github.com/arminzou/integrios/commit/7ba0e7374cc3f4041126227a764d2604ba5aba70))
* **admin:** keep a sheet open while its write is in flight ([634b3d2](https://github.com/arminzou/integrios/commit/634b3d228d68d91d6805b51e095d55cfb9727929))
* **admin:** re-read the session when an Admin call answers 401 ([51ffb95](https://github.com/arminzou/integrios/commit/51ffb95a2942ab3aed7ef8c8c6c30253c6e32fc8))
* **deploy:** give the Worker time to finish in-flight deliveries on stop ([1b097f2](https://github.com/arminzou/integrios/commit/1b097f26bcb07b9f73664ecda7b97869bb447bd0))
* **deploy:** mount Admin's Azure key ring as the app user ([65cad5e](https://github.com/arminzou/integrios/commit/65cad5ede68b1c9681422bdb3d8344196c5c3ce9))
* **deps:** Bump postgres in /deploy in the deploy-images group ([#125](https://github.com/arminzou/integrios/issues/125)) ([bbedc70](https://github.com/arminzou/integrios/commit/bbedc7060aeb67a2cc5dbe592093ed8050db17cf))
* **dev:** fail the seed scripts when the Delivery never dead-letters ([f932647](https://github.com/arminzou/integrios/commit/f93264710942a8e3cf8fc42858865f5b6cbc2612))
* **dev:** poll once more after the seed scripts' last dead-letter wait ([c81b3ef](https://github.com/arminzou/integrios/commit/c81b3efcc23290ed8324e6973fbcf52cadb03c83))
* **dev:** use the supported SQL Server image and keep Grafana local ([4411521](https://github.com/arminzou/integrios/commit/4411521c65479f5b66fc1fd27900c23f393bed17))
* give Admin and Ingestion their own default product listeners ([9ddfadc](https://github.com/arminzou/integrios/commit/9ddfadcda0e7b474cab3a99c40f4fe47bbfbfb42))
* **ingestion:** cap source event ids at 450 characters and match duplicates exactly ([6f8b98e](https://github.com/arminzou/integrios/commit/6f8b98e7bd28c10af7db43f94a4ae6b82e13ac16))
* **ingestion:** treat a blank identity as missing before checking its length ([460bcf1](https://github.com/arminzou/integrios/commit/460bcf1eac6069bfdf06de329101af6bfc15c6f8))
* keep the original error when an Event acceptance fails ([87c856e](https://github.com/arminzou/integrios/commit/87c856eb3429916807a082c119afab8feea765cf))
* recycle pooled connections in the Subscriber SDK and OAuth token clients ([5957bf6](https://github.com/arminzou/integrios/commit/5957bf66ef645563678efa6cd947e1e58c6056c4))
* report a mapping snapshot with a wrong field type as a transform failure ([97961fe](https://github.com/arminzou/integrios/commit/97961fede26a2c0683dc99a63a5be5ff8ced510a))
* report an undecodable mapping snapshot string as a transform failure ([409acfc](https://github.com/arminzou/integrios/commit/409acfc80db6126dde7037b006e0bf959e70cbec))
* report only transform failures as delivery mapping errors ([0934b29](https://github.com/arminzou/integrios/commit/0934b29387b74f691e8d802e89f3483a6d1f17ce))
* run every role as the image's non-root app user ([ad0c2b7](https://github.com/arminzou/integrios/commit/ad0c2b71611094056e022e4bb1ae25ad602e3ea1))
* **subscriber:** count pull settlements in the delivery outcome metrics ([a08c654](https://github.com/arminzou/integrios/commit/a08c6540c9a9f3b9c2b6aa53d5048f2db6c3e0d3))
* **subscriber:** load the role's development settings ([e968528](https://github.com/arminzou/integrios/commit/e968528de9753cb8ff88cd9c709a1893cb9b8162))
* **worker:** keep outbound clients from sharing cookies or buffering whole responses ([9d3d71f](https://github.com/arminzou/integrios/commit/9d3d71fe4950b549024d0c07e11b33eca1ae6cab))
* **worker:** stop the capped read at the cap when the length is declared ([93f2d28](https://github.com/arminzou/integrios/commit/93f2d284dc7fe90407ced97f6a1dd6780f1d27f6))

## [0.13.0](https://github.com/arminzou/integrios/compare/v0.12.0...v0.13.0) (2026-11-24)


### ⚠ BREAKING CHANGES

* **api:** address pull deliveries by lease token and rename nack to release ([#104](https://github.com/arminzou/integrios/issues/104))

### Features

* **admin:** author pull Destinations and Subscriptions ([#90](https://github.com/arminzou/integrios/issues/90)) ([740d6a5](https://github.com/arminzou/integrios/commit/740d6a5faf5a4352689b86a0600983ba2fdab787))
* **admin:** run deployment setup as one job and test delivery in-process ([#107](https://github.com/arminzou/integrios/issues/107)) ([310bc0c](https://github.com/arminzou/integrios/commit/310bc0c6c1f09e4ea1eaa3794330fff4c0da9351))
* **api:** add the delivery-api role for pull claims ([#92](https://github.com/arminzou/integrios/issues/92)) ([1b4667d](https://github.com/arminzou/integrios/commit/1b4667d07716a65cbcd7800adf9a06ac72bcf141))
* **api:** add the generic-host Subscriber SDK preview ([#98](https://github.com/arminzou/integrios/issues/98)) ([45112de](https://github.com/arminzou/integrios/commit/45112deb968e59e871c5cce55a5b68cd7dda510a))
* **api:** authenticate Subscribers with keys scoped to one pull Destination ([#93](https://github.com/arminzou/integrios/issues/93)) ([da163d2](https://github.com/arminzou/integrios/commit/da163d2b270f1029e8f7894013683bbd2012f6e4))
* **api:** serve the pull delivery protocol to Subscribers ([#94](https://github.com/arminzou/integrios/issues/94)) ([96e9f3f](https://github.com/arminzou/integrios/commit/96e9f3f428e5efa6cdacbd743c1fbb461bc7ab06))
* **worker:** add the pull delivery core for Subscribers ([#89](https://github.com/arminzou/integrios/issues/89)) ([c50c92a](https://github.com/arminzou/integrios/commit/c50c92aee46b9a6dcdcbbfb1b4699f89e7d9952c))


### Bug Fixes

* **ci:** fetch a pull request's event base when the shallow checkout lacks it ([#99](https://github.com/arminzou/integrios/issues/99)) ([8fd070d](https://github.com/arminzou/integrios/commit/8fd070d42f7fa912c51f0e1c8bdd61fae01af482))


### Code Refactoring

* **api:** address pull deliveries by lease token and rename nack to release ([#104](https://github.com/arminzou/integrios/issues/104)) ([d6f41a5](https://github.com/arminzou/integrios/commit/d6f41a5b2d4086b638d911d3e0fcf26e1d1e5b0a))

## [0.12.0](https://github.com/arminzou/integrios/compare/v0.11.0...v0.12.0) (2026-11-16)


### ⚠ BREAKING CHANGES

* **infra:** The Azure reference takes one image parameter in place of adminImage, ingestionImage and workerImage. A parameter file that pins its own build sets image to one <registry>.azurecr.io/<repository>@sha256:<digest> reference; a release-based file needs no change.
* **infra:** The ingestion, admin and worker images are replaced by one image, ghcr.io/<owner>/<repo>. Run it with the role as the first argument after the entrypoint, for example integrios ingestion, and start Admin's tools as integrios admin bootstrap or integrios admin database migrate. The /app/service entrypoint is gone.
* **infra:** Rename each host's appsettings.json to appsettings.<role>.json (ingestion, admin or worker), and appsettings.<Environment>.json to appsettings.<role>.<Environment>.json. Replace WorkerMetricsPort with OperationalPort. Outside a container, Ingestion's operational port now defaults to 5232 and Admin's to 5151.

### Features

* **api:** check webhook verification settings when a Source is saved ([c4bccc9](https://github.com/arminzou/integrios/commit/c4bccc90d95cdb9230e4a638a5f048cccee1c57a))
* **api:** verify webhooks that send a fixed token in a header ([#67](https://github.com/arminzou/integrios/issues/67)) ([c4bccc9](https://github.com/arminzou/integrios/commit/c4bccc90d95cdb9230e4a638a5f048cccee1c57a))
* **infra:** attach a self-contained Windows archive to each release ([#78](https://github.com/arminzou/integrios/issues/78)) ([6d09f27](https://github.com/arminzou/integrios/commit/6d09f2736fe3cac157214acb68e008921ac06381))
* **infra:** give each role its own settings files and operational port ([#75](https://github.com/arminzou/integrios/issues/75)) ([0aa7102](https://github.com/arminzou/integrios/commit/0aa7102ea72fe0bd37e5ecf1c7426c55b1c814fb))
* **infra:** run hosts as Windows services and systemd units ([#68](https://github.com/arminzou/integrios/issues/68)) ([738f481](https://github.com/arminzou/integrios/commit/738f481eee7bad67a4ee694f542ca106c9afea22))
* **infra:** run the Azure reference from the one image ([#77](https://github.com/arminzou/integrios/issues/77)) ([5a85996](https://github.com/arminzou/integrios/commit/5a859960dc6e9b3eadb76a6dfb76cc696555b7bb))
* **infra:** ship one image that starts any role ([#76](https://github.com/arminzou/integrios/issues/76)) ([d1e6a1b](https://github.com/arminzou/integrios/commit/d1e6a1b3d29e637d9cd7d25afac967a549e21dd0))
* **infra:** start each host as a role of one integrios executable ([#74](https://github.com/arminzou/integrios/issues/74)) ([303d2c6](https://github.com/arminzou/integrios/commit/303d2c61c3e9864f48fa5a49b6592d9c9bece115))


### Bug Fixes

* **deps:** Bump node ([5bc2b76](https://github.com/arminzou/integrios/commit/5bc2b762be322ccb5e13990833546b29ec695400))
* **infra:** match the Prometheus exporter to OpenTelemetry 1.20 ([5c43313](https://github.com/arminzou/integrios/commit/5c433135b82d2a4982dadf4b4e4fc1b69206343e))
* **infra:** ship .NET runtime 10.0.15 and keep it current ([#73](https://github.com/arminzou/integrios/issues/73)) ([3a832a1](https://github.com/arminzou/integrios/commit/3a832a1737c40cb11459d31028a5f5f25c444d67))
* **infra:** stop reading the key-per-file directory on Windows ([738f481](https://github.com/arminzou/integrios/commit/738f481eee7bad67a4ee694f542ca106c9afea22))

## [0.11.0](https://github.com/arminzou/integrios/compare/v0.10.0...v0.11.0) (2026-09-28)


### ⚠ BREAKING CHANGES

* **auth:** Deployments that omitted the password-login setting now expose password sign-in. Set INTEGRIOS_ADMIN_PASSWORD_ENABLED=false (Compose) or adminPasswordEnabled = false (Azure) before upgrading to keep it disabled.

### Features

* **auth:** create every Operator account with operator-user create ([b28c629](https://github.com/arminzou/integrios/commit/b28c629783af67cd4f590b3630c0d4da531c7f82))
* **auth:** enable password login by default with first-Operator setup ([a198293](https://github.com/arminzou/integrios/commit/a19829308f1bf0311a3906dea61821e08fbfcaad))
* **infra:** create the first Azure Operator interactively after deployment ([c39b47d](https://github.com/arminzou/integrios/commit/c39b47d66a768ce83e35c52b6fb6d8563805e392))


### Bug Fixes

* **cli:** reject invalid set-password options before prompting ([020cbb7](https://github.com/arminzou/integrios/commit/020cbb7a78401dd7fdd511aa02bd4cea49162c77))
* **infra:** read first-Operator status from Log Analytics ([b788bc9](https://github.com/arminzou/integrios/commit/b788bc9ebff14be796a656c8c2bb4e6250ab3d9a))
* **infra:** tolerate revisions already in the requested state ([465c51a](https://github.com/arminzou/integrios/commit/465c51a5b650acd9c4443943cc74e20d30bcef14))

## [0.10.0](https://github.com/arminzou/integrios/compare/v0.9.0...v0.10.0) (2026-09-26)


### ⚠ BREAKING CHANGES

* **db:** Packaged Compose runs services as separate runtime database users. Set INTEGRIOS_ADMIN_RUNTIME_PASSWORD and INTEGRIOS_DATA_RUNTIME_PASSWORD before upgrading.
* **infra:** Azure database access is Entra-only through managed identities. Before redeploying a 0.9.0 deployment on Azure SQL, make the Migrate identity the server's Entra administrator (az sql server ad-admin create) and enable Entra-only authentication (az sql server ad-only-auth enable). On PostgreSQL, enable Entra beside password authentication, make the Migrate identity an Entra administrator, then as integrios_admin run REASSIGN OWNED BY integrios_admin TO "id-<namePrefix>-migrate".

### Features

* **db:** separate runtime database privileges and remove Azure database passwords ([#44](https://github.com/arminzou/integrios/pull/44)) ([e2df441](https://github.com/arminzou/integrios/commit/e2df44152faa7d802fd81e73b5cc673c48d3a5c7))
* **infra:** deploy an Azure release by version instead of digests ([50df178](https://github.com/arminzou/integrios/commit/50df178da753822d019dc7717fcdbde0cba5aadb))
* **infra:** generate and reuse Azure deployment secrets ([5a97f74](https://github.com/arminzou/integrios/commit/5a97f749d83f41cbbe58d1e011ede7f1ba3bad37))


### Bug Fixes

* **infra:** preserve stored deployment secrets exactly ([ae094a8](https://github.com/arminzou/integrios/commit/ae094a8cbacacfbcd3d19b964718796a4a1ea139))

## [0.9.0](https://github.com/arminzou/integrios/compare/v0.8.0...v0.9.0) (2026-09-24)


### ⚠ BREAKING CHANGES

* **infra:** Azure deployments must move Tenant secrets to the new vaults and remove the old mapping and secret parameters.
* The provider switch and per-Tenant file layout are removed. Use kebab-case references and run secrets validate --all after upgrading.
* **infra:** Mounted secrets now use per-process directories with SourceSecrets__ or DestinationSecrets__ file names. Rotation requires a restart.
* **admin:** require kebab-case secret references
* **infra:** resolve Tenant secrets from standard .NET configuration

### Features

* **admin:** guide OAuth2 destination authoring ([d7d097f](https://github.com/arminzou/integrios/commit/d7d097fddb5f704e55f081d9097f0a4904cc4504))
* **admin:** persist the Data Protection key ring on shared storage ([9f975ee](https://github.com/arminzou/integrios/commit/9f975ee9ae90093a9291b019ae69fe1f6bf6abb7))
* **admin:** require kebab-case secret references ([bc86857](https://github.com/arminzou/integrios/commit/bc868571942ce3c0b72eea51021cae73235a7367))
* **infra:** mount tenant secrets per process ([42c44f8](https://github.com/arminzou/integrios/commit/42c44f842fe7540e49395dae22eae766ee853835))
* **infra:** resolve Tenant secrets from standard .NET configuration ([317b6e2](https://github.com/arminzou/integrios/commit/317b6e280ab48c2b52b69f20e986fec062f519a9))
* **infra:** use separate Azure vaults for tenant secrets ([f66bf97](https://github.com/arminzou/integrios/commit/f66bf97ccec353a4128d866b1a87194c857d463f))
* **infra:** use user-assigned identities in Azure apps ([9379946](https://github.com/arminzou/integrios/commit/9379946fdf562c4281e6eb18a43844dbbe84bb86))
* **infra:** wire dashboard sign-in into the Azure reference ([69cde08](https://github.com/arminzou/integrios/commit/69cde08fabe80943ba2e7716f78103cbaadaab68))
* **worker:** add database-coordinated completed-history cleanup ([16e894d](https://github.com/arminzou/integrios/commit/16e894dc53b6ab1df029277841d69cb49bb1668d))
* **worker:** add OAuth2 token acquisition ([a555ded](https://github.com/arminzou/integrios/commit/a555ded0daaa5bf89184e1c0f09d5242655fffea))
* **worker:** document retention rollout ([df3036b](https://github.com/arminzou/integrios/commit/df3036b10cbef2630cc94fb71ed93e794b412fdd))
* **worker:** run completed-history retention ([6564e5c](https://github.com/arminzou/integrios/commit/6564e5c2d10d7dd2f7fbd894ddf32377ef946fb7))


### Bug Fixes

* **infra:** close configuration secrets review findings ([ce284a6](https://github.com/arminzou/integrios/commit/ce284a636ab073e440733dad13ba349a25c388be))
* **infra:** preserve Workbook counter series labels ([a0c3c5f](https://github.com/arminzou/integrios/commit/a0c3c5fb13e82b3a069358c286bde01f8589c036))
* **infra:** remove obsolete image build arguments ([fb125e4](https://github.com/arminzou/integrios/commit/fb125e4d91b77ff925aa89ce52be8272cddff7c8))
* **infra:** retry Azure secret access propagation ([eef1632](https://github.com/arminzou/integrios/commit/eef1632d7349e5b8582fb2e6cf790f3a30255628))
* **worker:** close OAuth delivery review findings ([dfe8f9a](https://github.com/arminzou/integrios/commit/dfe8f9a4e55bb008fa93d76181b81b8e8f128ba6))
* **worker:** harden completed-history cleanup ([6c24afa](https://github.com/arminzou/integrios/commit/6c24afa6c657bfeb6e0dbf0dd5db8d79ce6e67a3))
* **worker:** harden retention database compatibility ([372d517](https://github.com/arminzou/integrios/commit/372d517858b84741409c3cd1a47edf39d73c5630))
* **worker:** retry SQL Server finalization deadlocks ([0da3bf1](https://github.com/arminzou/integrios/commit/0da3bf10ba37cb5ce9e450eb53e7d9e29cf40368))


### Documentation

* document configuration-backed tenant secrets ([705ab17](https://github.com/arminzou/integrios/commit/705ab17a723790567d9e82d39b79e3ba85801121))

## [0.8.0](https://github.com/arminzou/integrios/compare/v0.7.0...v0.8.0) (2026-09-21)


### Features

* **admin:** add revoked key deletion ([5d72f15](https://github.com/arminzou/integrios/commit/5d72f152a6f33e83121fdd124cfbc58285cd9175))
* **admin:** declare Event types first and lighten Source authoring ([3d84f76](https://github.com/arminzou/integrios/commit/3d84f763db5c8b56525c91a34874fed7b6dd8a81))
* **admin:** keep revoked Tenant API keys visible and drop key expiry ([5a3bc10](https://github.com/arminzou/integrios/commit/5a3bc10d263363d49d8b68f91630186d386a1484))
* **admin:** let the Mapping Playground input pane own the sample and preview live ([690818b](https://github.com/arminzou/integrios/commit/690818b5f7222ced9422901593d8ddc447e30d6a))
* **admin:** refine Inspector layout, status tones and Topic authoring ([ec5ba3f](https://github.com/arminzou/integrios/commit/ec5ba3fc286e737ae97363619909bd8be4e06ca4))

## [0.7.0](https://github.com/arminzou/integrios/compare/v0.6.0...v0.7.0) (2026-09-20)


### ⚠ BREAKING CHANGES

* **api:** rename the broker-backed Source type from queue to broker

### Features

* **admin:** add progressive source journeys ([0519e00](https://github.com/arminzou/integrios/commit/0519e00de304a11ec3221ba21fad195e9ffb2bcb))
* **admin:** apply Events filters live and retire the old filter code ([fa2ec88](https://github.com/arminzou/integrios/commit/fa2ec88202c5b036bcf5641757343328a7b6682c))
* **admin:** author both Source Event decisions in the Builder ([1133c72](https://github.com/arminzou/integrios/commit/1133c72081e2abcf5ff0f6f0d37647a2958f8493))
* **admin:** author New Source through guided fields, not JSON ([bcb4f22](https://github.com/arminzou/integrios/commit/bcb4f22cba6a0290672b3896b1cc2cf761668f8e))
* **admin:** chart Event activity by current outcome over 1 h, 24 h, or 7 d ([f8fb61e](https://github.com/arminzou/integrios/commit/f8fb61eda307564ae015454f174b9f1a3e69d882))
* **admin:** clarify Source event normalization ([69cb7f8](https://github.com/arminzou/integrios/commit/69cb7f836eb85423c839c2b2d631ad7a4ca5b9c7))
* **admin:** create a Subscription from an unrouted Event ([296094d](https://github.com/arminzou/integrios/commit/296094daa635308659c297b7ea19e173c18bcd95))
* **admin:** edit Source Event identity ([7af80e3](https://github.com/arminzou/integrios/commit/7af80e37e2a6b797909d5bcb201fbef469ee7e84))
* **admin:** edit Source verification ([840b460](https://github.com/arminzou/integrios/commit/840b4602c5f89f47e2100fd532656326dd2eb474))
* **admin:** enforce prospective delivery lifecycle ([da6c7e9](https://github.com/arminzou/integrios/commit/da6c7e93a0edf7fa87a27f3eb927a4f4093352fd))
* **admin:** explain historical-only unrouted Events and link traces ([b3b7bf7](https://github.com/arminzou/integrios/commit/b3b7bf7a65364e51366f60a0ce6e481613ff96f1))
* **admin:** filter the Event history by event type ([c11ba5d](https://github.com/arminzou/integrios/commit/c11ba5d224e4c8a0ff7c23426466f15c1efcda0d))
* **admin:** filter the ledger by Event type and count new Events without moving rows ([57da8f9](https://github.com/arminzou/integrios/commit/57da8f9777bc71c0298f08d1aa1be83be18af507))
* **admin:** find an Event by one box with a field chooser ([dfb57c2](https://github.com/arminzou/integrios/commit/dfb57c2db7751f3e99f01212928b1e809f5f36c6))
* **admin:** give Sources, Destinations, and Subscriptions an Enabled or Disabled status ([5053e39](https://github.com/arminzou/integrios/commit/5053e395931379fe6b5752a66191208a6709b40f))
* **admin:** give the remaining list screens one filter bar model ([846208e](https://github.com/arminzou/integrios/commit/846208ec1520aaae24f5f9ab74e01c1a5fe6179a))
* **admin:** guide Destination and Subscription authoring ([f72753d](https://github.com/arminzou/integrios/commit/f72753d8a5bbcedd3ce4e2a11d0c6b886def8e30))
* **admin:** guide Source configuration edits ([a1b3b9e](https://github.com/arminzou/integrios/commit/a1b3b9ee9485e5e04e94e7270893365d072ab993))
* **admin:** guide Source Event contract edits ([294e239](https://github.com/arminzou/integrios/commit/294e239459aaf78c65b186d686ac7ddf9947af50))
* **admin:** hand off a new API key and confirm every copy ([c31eaef](https://github.com/arminzou/integrios/commit/c31eaef60449c4eca68a23099e364ab764e68b4a))
* **admin:** import a cURL command into the Event Builder sample ([7ec2120](https://github.com/arminzou/integrios/commit/7ec212096b2e9b6e376673e029de1ba1d24e436d))
* **admin:** keep the Event Builder's actions in view ([6ac8f2c](https://github.com/arminzou/integrios/commit/6ac8f2c88bd3ca8466d52e7cdeab2da8724fba4e))
* **admin:** make a Source declare the Event types it may publish ([c6ffd54](https://github.com/arminzou/integrios/commit/c6ffd5480a3c9557dc7c260876a1fd04d32d611f))
* **admin:** name operational status Active or Inactive ([f537c7a](https://github.com/arminzou/integrios/commit/f537c7a12da762699f8a4ee4a240a5d31088fc2a))
* **admin:** read every shown code panel as code ([a7f10a9](https://github.com/arminzou/integrios/commit/a7f10a9dd8c602d64f93bd0c24002ffcd82dd0c2))
* **admin:** read Subscription filters through a shared list-filter hook ([5a6144b](https://github.com/arminzou/integrios/commit/5a6144b2d04d633ed1575c3a203299fad5fc3763))
* **admin:** read the Event Builder's sample body as code ([5a1b231](https://github.com/arminzou/integrios/commit/5a1b2313f81e22ff8153818b6848a9c61b8987da))
* **admin:** reduce the Event Builder to the Event type decision ([641ddc7](https://github.com/arminzou/integrios/commit/641ddc7d0fdbf12a4355f93288ee2eb4733d9315))
* **admin:** reduce the Event Builder to two decisions and a verdict ([cc035ca](https://github.com/arminzou/integrios/commit/cc035cacb57a7ffad988699e0962e8548c3d22b7))
* **admin:** replace the Events accepted-time inputs with one Accepted pill ([80d99b9](https://github.com/arminzou/integrios/commit/80d99b9e0e7393e7cb6c09a01cb5e131eae53bc7))
* **admin:** report current Event backlogs however old ([0d2eb1f](https://github.com/arminzou/integrios/commit/0d2eb1f75ee469eb33a6423bb739b98b312ad992))
* **admin:** retain deleted resource provenance ([d5e9a3e](https://github.com/arminzou/integrios/commit/d5e9a3e782a232f5d1ad67ee502d0f965c487779))
* **admin:** route Subscriptions by Event types their Topic's Sources declare ([c3fdfda](https://github.com/arminzou/integrios/commit/c3fdfda58dfd3e1a4fc54152a4f80f2fc58f6a57))
* **admin:** serialize declaration authoring per Topic ([449b0a2](https://github.com/arminzou/integrios/commit/449b0a23a3fbcffcf11a18c9e7ba476b0736f444))
* **admin:** show an unset filter as its label alone and tidy the pills ([c531124](https://github.com/arminzou/integrios/commit/c531124c2af6495373f3a8b320dfda07e560dc66))
* **admin:** show and edit every code document the same way ([6de68ff](https://github.com/arminzou/integrios/commit/6de68ff1ccd73650bf8989afd323df47e562b84c))
* **admin:** simplify Source advanced authoring ([f905ece](https://github.com/arminzou/integrios/commit/f905ece7c18ccadef898ad9c6337197f4185dea8))
* **admin:** step the Mapping Playground through the Subscription's own Events ([71a93c5](https://github.com/arminzou/integrios/commit/71a93c50b2168ca1cc2e4db21c721636e6dff9e7))
* **api:** count webhook verification secret resolution failures ([6d9e1b5](https://github.com/arminzou/integrios/commit/6d9e1b558e820f2e4ab274593d6ced1d347413a8))
* **api:** let the Source contract preview check any Source ([abfd303](https://github.com/arminzou/integrios/commit/abfd303c78196b4195d717142af25053fe27f5d6))
* **api:** refuse Event types that fail silently ([26ebb81](https://github.com/arminzou/integrios/commit/26ebb81650ec24167756d66e9e0792c845cc7b92))
* **api:** rename the broker-backed Source type from queue to broker ([03f312e](https://github.com/arminzou/integrios/commit/03f312e7ba6800a583f3fc864e579656881a6d75))
* **api:** resolve the Event identity in the Source contract preview ([d045d8c](https://github.com/arminzou/integrios/commit/d045d8cdbe6cea607360da08df85a05e72955c67))
* **core:** let a Source Event identity permit a missing value ([7346668](https://github.com/arminzou/integrios/commit/7346668f706957595655f18bfcc7b3cb954098c1))
* **core:** make Source Event identity replaceable ([a79d7e6](https://github.com/arminzou/integrios/commit/a79d7e6e2724589e7ec72656ebc03e5c265a5bd6))
* **ingestion:** refuse Events a Source does not declare or may not publish now ([b7dcea8](https://github.com/arminzou/integrios/commit/b7dcea811cd13bd11ac47bafffeae80041d542ee))
* **scripts:** add a PowerShell twin of the dev data seed ([0580764](https://github.com/arminzou/integrios/commit/0580764e002653f1006217a2043b50927ffab425))


### Bug Fixes

* **admin:** address review of the live filter model ([fc5db2b](https://github.com/arminzou/integrios/commit/fc5db2b3fa96bb71c98ed9697b286ebe8daf3ff7))
* **admin:** apply Activity gestures once and keep the backlog current ([0efb726](https://github.com/arminzou/integrios/commit/0efb7268db17712f17f2e7cb272fdb5b0cdf009c))
* **admin:** clear remediated unrouted backlog ([e4688a6](https://github.com/arminzou/integrios/commit/e4688a6335cb138e71b1e9b7d5d128c7ee79acfd))
* **admin:** discard Event Builder state on close ([007e9f2](https://github.com/arminzou/integrios/commit/007e9f295bc2d9df9987b7d888dc677ce3074e6e))
* **admin:** hide non-authorable Source details ([67b46bb](https://github.com/arminzou/integrios/commit/67b46bb0735f7c325822bf00ed397ccd97df7815))
* **admin:** hide Tenant API key expiration ([97c3d2f](https://github.com/arminzou/integrios/commit/97c3d2facba42c13365a70b85c6f606e3c286a03))
* **admin:** hold the page steady while the Event inspector swaps selection ([3bfd9c8](https://github.com/arminzou/integrios/commit/3bfd9c88fe73e62fdbc272d8fcf16ba561c1bcd3))
* **admin:** keep autofill and spell-check off the filter search boxes ([fdad3da](https://github.com/arminzou/integrios/commit/fdad3da2cc55a62f4cf29c49cf94ec5c1a4cdbd4))
* **admin:** keep the ledger's accepted range as exact instants ([edc65ab](https://github.com/arminzou/integrios/commit/edc65aba957d3d637b19753cb77169ae79505b19))
* **admin:** lay out Subscription authoring as one flat request form ([755556f](https://github.com/arminzou/integrios/commit/755556f5e24f6d4f83f7a1888f3234112133e08f))
* **admin:** let an advanced Source mapping leave the Builder ([b708d2e](https://github.com/arminzou/integrios/commit/b708d2e2aa31ed747836f7282f15ee26c5dcd000))
* **admin:** normalize conflicting Event identity filters ([dcaf265](https://github.com/arminzou/integrios/commit/dcaf265466d46a186ff1dd54809b4e1bb88f1aef))
* **admin:** offer the missing-identity permission for every identity kind ([c8bba81](https://github.com/arminzou/integrios/commit/c8bba813a4c7ec93af6bb153f0fb418a71ed46f6))
* **admin:** outline the focused Activity interval so keyboard focus stays visible on its bars ([b549a6f](https://github.com/arminzou/integrios/commit/b549a6f14cd5fdeb137bf1af84aa9ff879fd0f68))
* **admin:** pin Event type matching and count every Event after an empty ledger page ([80f71f6](https://github.com/arminzou/integrios/commit/80f71f62f2d4f4259e29b49a53042ef7ab8ea42d))
* **admin:** put the identity-header typo on the Source, not the request ([6b5e781](https://github.com/arminzou/integrios/commit/6b5e781024f2a770580d0482f992b60a87cdbd53))
* **admin:** release deleted Topic keys and serialize Subscription activation ([667c331](https://github.com/arminzou/integrios/commit/667c331ee7ec48591553cc1b50925d34ed7b4927))
* **admin:** remove Tenant API key expiration input ([2da60de](https://github.com/arminzou/integrios/commit/2da60de46cb5f4c6e0a8240eef715964353d6180))
* **admin:** repiar Admin.http request and seed-dev-data request contract drift ([bc95cba](https://github.com/arminzou/integrios/commit/bc95cbad0b67e9331164267aabada5af2008af02))
* **admin:** require explicit Source type selection ([a640410](https://github.com/arminzou/integrios/commit/a64041013aac32ba6a23fe6b07cf3f2a6ee8472a))
* **admin:** show what the Event Builder owns as one thing ([b7f1f19](https://github.com/arminzou/integrios/commit/b7f1f1969d15f70de6ff9c687b3e34dff29ec610))
* **api:** map an unresolvable Source secret to a deliberate response ([04178ee](https://github.com/arminzou/integrios/commit/04178eebe958f52554128bab4d0178db76acf7d8))
* **core:** reject a Source Event identity header no request can carry ([eed5ffa](https://github.com/arminzou/integrios/commit/eed5ffaa76460e20990be32181d8e2a609fdc099))
* **infra:** borrow the provider's transient-fault classification ([0a8c3cb](https://github.com/arminzou/integrios/commit/0a8c3cb1a12df4b7d688e809f12449feed1b65d4))
* **infra:** retry transient database faults on both providers ([62839c1](https://github.com/arminzou/integrios/commit/62839c1cde1fbbac267a46b5e4ee48c232925bbd))
* **ingestion:** refuse bodies that are not valid UTF-8 as malformed input ([b7121e2](https://github.com/arminzou/integrios/commit/b7121e224b1f08ab5bca740e99ea640a549ae930))
* **ingestion:** stop all intake when a Tenant is deactivated ([b4f9cf9](https://github.com/arminzou/integrios/commit/b4f9cf947a49420ac14ce8bf805aa9e9ea02cef2))
* **scripts:** seed through activate endpoints and make the queue demo opt-in ([d47ac11](https://github.com/arminzou/integrios/commit/d47ac1168ba43c1dac1caed7bc89bc24b03f4401))

## [0.6.0](https://github.com/arminzou/integrios/compare/v0.5.0...v0.6.0) (2026-09-14)


### Features

* **admin:** add OIDC-first operator login gate ([2cd3def](https://github.com/arminzou/integrios/commit/2cd3defee2c475073f70e7cb036ecb479b018481))
* **admin:** add operator password credential lifecycle ([17bc282](https://github.com/arminzou/integrios/commit/17bc282d3d488cb94998fadf4da95e538d2d5a1f))
* **admin:** add operator password login ([b24ce94](https://github.com/arminzou/integrios/commit/b24ce94a0a6ed4736aba2c0225bdad0e75419236))


### Bug Fixes

* **admin:** explain an unconfigured deployment to the browser ([e40a048](https://github.com/arminzou/integrios/commit/e40a048739f24a4532095ab48b07391e6b89c410))
* **infra:** seed dev data through the destinations API ([555ef57](https://github.com/arminzou/integrios/commit/555ef579a9c7b9939d13b057a6e698985ee38b66))
