# Changelog

## [0.7.0](https://github.com/arminzou/integrios/compare/v0.6.0...v0.7.0) (2026-09-16)


### ⚠ BREAKING CHANGES

* **api:** rename the broker-backed Source type from queue to broker

### Features

* **admin:** author New Source through guided fields, not JSON ([bcb4f22](https://github.com/arminzou/integrios/commit/bcb4f22cba6a0290672b3896b1cc2cf761668f8e))
* **admin:** clarify Source event normalization ([69cb7f8](https://github.com/arminzou/integrios/commit/69cb7f836eb85423c839c2b2d631ad7a4ca5b9c7))
* **admin:** edit Source Event identity ([7af80e3](https://github.com/arminzou/integrios/commit/7af80e37e2a6b797909d5bcb201fbef469ee7e84))
* **admin:** edit Source verification ([840b460](https://github.com/arminzou/integrios/commit/840b4602c5f89f47e2100fd532656326dd2eb474))
* **admin:** guide Source configuration edits ([a1b3b9e](https://github.com/arminzou/integrios/commit/a1b3b9ee9485e5e04e94e7270893365d072ab993))
* **admin:** guide Source Event contract edits ([294e239](https://github.com/arminzou/integrios/commit/294e239459aaf78c65b186d686ac7ddf9947af50))
* **admin:** simplify Source advanced authoring ([f905ece](https://github.com/arminzou/integrios/commit/f905ece7c18ccadef898ad9c6337197f4185dea8))
* **api:** count webhook verification secret resolution failures ([6d9e1b5](https://github.com/arminzou/integrios/commit/6d9e1b558e820f2e4ab274593d6ced1d347413a8))
* **api:** rename the broker-backed Source type from queue to broker ([03f312e](https://github.com/arminzou/integrios/commit/03f312e7ba6800a583f3fc864e579656881a6d75))
* **core:** let a Source Event identity permit a missing value ([7346668](https://github.com/arminzou/integrios/commit/7346668f706957595655f18bfcc7b3cb954098c1))
* **core:** make Source Event identity replaceable ([a79d7e6](https://github.com/arminzou/integrios/commit/a79d7e6e2724589e7ec72656ebc03e5c265a5bd6))


### Bug Fixes

* **admin:** hide non-authorable Source details ([67b46bb](https://github.com/arminzou/integrios/commit/67b46bb0735f7c325822bf00ed397ccd97df7815))
* **admin:** hide Tenant API key expiration ([97c3d2f](https://github.com/arminzou/integrios/commit/97c3d2facba42c13365a70b85c6f606e3c286a03))
* **admin:** offer the missing-identity permission for every identity kind ([c8bba81](https://github.com/arminzou/integrios/commit/c8bba813a4c7ec93af6bb153f0fb418a71ed46f6))
* **admin:** put the identity-header typo on the Source, not the request ([6b5e781](https://github.com/arminzou/integrios/commit/6b5e781024f2a770580d0482f992b60a87cdbd53))
* **admin:** remove Tenant API key expiration input ([2da60de](https://github.com/arminzou/integrios/commit/2da60de46cb5f4c6e0a8240eef715964353d6180))
* **admin:** repiar Admin.http request and seed-dev-data request contract drift ([bc95cba](https://github.com/arminzou/integrios/commit/bc95cbad0b67e9331164267aabada5af2008af02))
* **api:** map an unresolvable Source secret to a deliberate response ([04178ee](https://github.com/arminzou/integrios/commit/04178eebe958f52554128bab4d0178db76acf7d8))
* **core:** reject a Source Event identity header no request can carry ([eed5ffa](https://github.com/arminzou/integrios/commit/eed5ffaa76460e20990be32181d8e2a609fdc099))
* **infra:** borrow the provider's transient-fault classification ([0a8c3cb](https://github.com/arminzou/integrios/commit/0a8c3cb1a12df4b7d688e809f12449feed1b65d4))
* **infra:** retry transient database faults on both providers ([62839c1](https://github.com/arminzou/integrios/commit/62839c1cde1fbbac267a46b5e4ee48c232925bbd))

## [0.6.0](https://github.com/arminzou/integrios/compare/v0.5.0...v0.6.0) (2026-09-14)


### Features

* **admin:** add OIDC-first operator login gate ([2cd3def](https://github.com/arminzou/integrios/commit/2cd3defee2c475073f70e7cb036ecb479b018481))
* **admin:** add operator password credential lifecycle ([17bc282](https://github.com/arminzou/integrios/commit/17bc282d3d488cb94998fadf4da95e538d2d5a1f))
* **admin:** add operator password login ([b24ce94](https://github.com/arminzou/integrios/commit/b24ce94a0a6ed4736aba2c0225bdad0e75419236))


### Bug Fixes

* **admin:** explain an unconfigured deployment to the browser ([e40a048](https://github.com/arminzou/integrios/commit/e40a048739f24a4532095ab48b07391e6b89c410))
* **infra:** seed dev data through the destinations API ([555ef57](https://github.com/arminzou/integrios/commit/555ef579a9c7b9939d13b057a6e698985ee38b66))
