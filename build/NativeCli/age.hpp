#pragma once

#include <filesystem>
#include <string>
#include <string_view>

bool age_envelope(std::string_view bytes);
bool age_open(std::string_view bytes, const std::filesystem::path& identity, std::string& plaintext);
