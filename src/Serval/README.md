# Serval

An async web framework for .NET with controller routing, middleware, static files, and streaming. It fits into
`Microsoft.Extensions.Hosting`; add a transport package to serve HTTP requests.

Register request-scoped `RequestLifecycleHandler` classes with `AddRequestLifecycleHandler<T>()` for request
initialization, final response headers, and completion outcomes. Use middleware to wrap downstream execution or
short-circuit a request. The main README explains phase ordering, priorities, callbacks, and cleanup, with separate
Middleware and Lifecycles samples.

See the [main README](https://github.com/depthbomb/serval/blob/master/README.md) for setup, examples, and the other
packages.
