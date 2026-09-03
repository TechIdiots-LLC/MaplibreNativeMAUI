/**
 * mln_cabi_internal.hpp — helpers shared between the mln-cabi translation
 * units (defined in mln_cabi.cpp).
 */
#pragma once

#include "mln_cabi.h"

#include <exception>
#include <string>

/** Store a diagnostic in the thread-local last-error slot and return @p code. */
mln_status_t cabi_set_error(mln_status_t code, std::string msg) noexcept;

/** Store e.what() in the thread-local last-error slot and return MLN_NATIVE_ERROR. */
mln_status_t cabi_set_native_error(const std::exception& e) noexcept;

/** Copy @p s into a new[]'d buffer the caller frees with mln_free_string(). */
char* cabi_dup_string(const std::string& s);
