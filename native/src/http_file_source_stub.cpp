// Minimal stub for HTTPFileSource used in standalone NDK builds.
// HTTP requests are not supported; each request immediately returns a
// Connection error so the map can fall back to cached / asset resources.
#include <mln/storage/http_file_source.hpp>
#include <mln/storage/resource.hpp>
#include <mln/storage/resource_options.hpp>
#include <mln/storage/response.hpp>
#include <mln/util/async_request.hpp>
#include <mln/util/client_options.hpp>

#include <memory>

namespace mln {

class HTTPFileSource::Impl {};

HTTPFileSource::HTTPFileSource(const ResourceOptions&, const ClientOptions&)
    : impl(std::make_unique<Impl>()) {}

HTTPFileSource::~HTTPFileSource() = default;

std::unique_ptr<AsyncRequest> HTTPFileSource::request(const Resource&, Callback callback) {
    Response response;
    response.error = std::make_unique<const Response::Error>(
        Response::Error::Reason::Connection, "HTTP not available in this build");
    callback(std::move(response));
    return nullptr;
}

void HTTPFileSource::setResourceOptions(ResourceOptions) {}

ResourceOptions HTTPFileSource::getResourceOptions() {
    return {};
}

void HTTPFileSource::setClientOptions(ClientOptions) {}

ClientOptions HTTPFileSource::getClientOptions() {
    return {};
}

} // namespace mln
