/**
 * mln_cabi_internal.hpp — helpers shared between the mln-cabi translation
 * units (defined in mln_cabi.cpp).
 */
#pragma once

#include "mln_cabi.h"

#include <exception>
#include <string>

/** Store a diagnostic in the thread-local last-error slot and return @p code. */
mbgl_status_t cabi_set_error(mbgl_status_t code, std::string msg) noexcept;

/** Store e.what() in the thread-local last-error slot and return MBGL_NATIVE_ERROR. */
mbgl_status_t cabi_set_native_error(const std::exception& e) noexcept;

/** Copy @p s into a new[]'d buffer the caller frees with mbgl_free_string(). */
char* cabi_dup_string(const std::string& s);
