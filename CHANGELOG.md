# Changelog

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
