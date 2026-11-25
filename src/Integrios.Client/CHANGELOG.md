# Changelog

## [0.3.0](https://github.com/arminzou/integrios/compare/client-v0.2.1...client-v0.3.0) (2026-11-25)


### ⚠ BREAKING CHANGES

* **client:** code that constructs Delivery positionally or deconstructs it must use object initializers and property access instead.

### Features

* **client:** make Delivery a record of required init-only properties ([afda8cd](https://github.com/arminzou/integrios/commit/afda8cd20e4ee8511573129325b362f23840169e))
* **client:** ship XML docs, validate the package, and lower its dependency floors ([4297e90](https://github.com/arminzou/integrios/commit/4297e90cb87e47e226b9c5141af2d28a494556e9))


### Bug Fixes

* recycle pooled connections in the Subscriber SDK and OAuth token clients ([5957bf6](https://github.com/arminzou/integrios/commit/5957bf66ef645563678efa6cd947e1e58c6056c4))

## [0.2.1](https://github.com/arminzou/integrios/compare/client-v0.2.0...client-v0.2.1) (2026-11-24)


### Documentation

* **client:** state the server version the SDK requires ([d0c2ff1](https://github.com/arminzou/integrios/commit/d0c2ff1488c4bb6fa8c29ba3fede23fdabed056f))

## [0.2.0](https://github.com/arminzou/integrios/compare/client-v0.1.0...client-v0.2.0) (2026-11-20)


### ⚠ BREAKING CHANGES

* **api:** address pull deliveries by lease token and rename nack to release ([#104](https://github.com/arminzou/integrios/issues/104))

### Code Refactoring

* **api:** address pull deliveries by lease token and rename nack to release ([#104](https://github.com/arminzou/integrios/issues/104)) ([d6f41a5](https://github.com/arminzou/integrios/commit/d6f41a5b2d4086b638d911d3e0fcf26e1d1e5b0a))
