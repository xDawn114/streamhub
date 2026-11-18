// Included inside namespace video, after validate_encoder(). Windows metadata cache.
#ifdef _WIN32
  static std::atomic_bool streamhub_persisted_ready {false};
  static std::string streamhub_sha_text(std::string_view bytes) {
    const auto hash = crypto::hash(bytes);
    constexpr char hex[] = "0123456789abcdef";
    std::string result;
    for (auto byte : hash) { result += hex[byte >> 4]; result += hex[byte & 15]; }
    return result;
  }

  static std::string streamhub_hash_file(const std::filesystem::path &path, std::uintmax_t maximum) {
    std::error_code error;
    const auto size = std::filesystem::file_size(path, error);
    if (error || size == 0 || size > maximum) return {};
    std::ifstream file(path, std::ios::binary);
    if (!file) return {};
    std::unique_ptr<EVP_MD_CTX, decltype(&EVP_MD_CTX_free)> context(EVP_MD_CTX_new(), EVP_MD_CTX_free);
    if (!context || EVP_DigestInit_ex(context.get(), EVP_sha256(), nullptr) != 1) return {};
    std::array<char, 65536> buffer;
    while (file) {
      file.read(buffer.data(), buffer.size());
      if (file.gcount() && EVP_DigestUpdate(context.get(), buffer.data(), file.gcount()) != 1) return {};
    }
    if (!file.eof()) return {};
    std::array<unsigned char, 32> digest;
    unsigned int length = 0;
    if (EVP_DigestFinal_ex(context.get(), digest.data(), &length) != 1 || length != digest.size()) return {};
    constexpr char hex[] = "0123456789abcdef";
    std::string result;
    for (auto byte : digest) { result += hex[byte >> 4]; result += hex[byte & 15]; }
    return result;
  }

  static std::filesystem::path streamhub_persisted_path() {
    // A new sidecar in the selected product profile. Never edits the state file.
    return std::filesystem::path(config::nvhttp.file_state).concat(".probe-cache-v1");
  }

  static bool streamhub_cache_path_allowed(const std::filesystem::path &path) {
    if (path.empty() || !path.is_absolute()) return false;
    auto current = path;
    bool leaf = true;
    while (!current.empty()) {
      const auto attributes = GetFileAttributesW(current.c_str());
      if (attributes == INVALID_FILE_ATTRIBUTES) {
        if (!leaf || (GetLastError() != ERROR_FILE_NOT_FOUND && GetLastError() != ERROR_PATH_NOT_FOUND)) return false;
      } else if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) || (leaf && (attributes & FILE_ATTRIBUTE_DIRECTORY))) {
        return false;
      }
      const auto parent = current.parent_path();
      if (parent == current) break;
      current = parent; leaf = false;
    }
    return true;
  }

  static std::string streamhub_persisted_fingerprint(const std::string &environment) {
    if (environment.empty()) return {};
    static const std::string image_hash = [] {
      std::array<wchar_t, 32768> name {};
      const auto length = GetModuleFileNameW(nullptr, name.data(), name.size());
      return length && length < name.size() ? streamhub_hash_file(std::filesystem::path(name.data()), 256 * 1024 * 1024) : std::string {};
    }();
    const auto configuration_hash = streamhub_hash_file(config::sunshine.config_file, 1024 * 1024);
    // A file changed under an already-running process is not its parsed config.
    static const auto startup_configuration_hash = configuration_hash;
    if (image_hash.empty() || configuration_hash.empty() || configuration_hash != startup_configuration_hash) return {};
    const std::wstring command = GetCommandLineW();
    const auto command_hash = streamhub_sha_text(std::string_view(reinterpret_cast<const char *>(command.data()), command.size() * sizeof(wchar_t)));
    return streamhub_sha_text("SHPC1:" + image_hash + ':' + configuration_hash + ':' + command_hash + ':' + environment);
  }

  static std::int64_t streamhub_wall_seconds() {
    return std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
  }

  static void streamhub_discard_persisted_probe() {
    streamhub_persisted_ready = false;
    const auto path = streamhub_persisted_path();
    if (streamhub_cache_path_allowed(path)) DeleteFileW(path.c_str());
  }

  static bool streamhub_restore_persisted_probe(const std::string &environment, const std::string &output_name) {
    streamhub::timer duration("persistent-probe-read-and-verify");
    const auto path = streamhub_persisted_path();
    const auto fingerprint = streamhub_persisted_fingerprint(environment);
    if (fingerprint.empty() || !streamhub_cache_path_allowed(path)) return false;
    std::error_code error;
    const auto size = std::filesystem::file_size(path, error);
    if (error || size == 0 || size > 4096) return false;
    std::ifstream input(path, std::ios::binary);
    std::string bytes(static_cast<std::size_t>(size), '\0');
    if (!input.read(bytes.data(), bytes.size()) || input.peek() != std::char_traits<char>::eof()) return false;
    // Windows ifstream does not share delete access. Release the read handle
    // before the failure guard invalidates a record after real sample failure.
    input.close();
    const auto saved = streamhub::decode_persisted_probe(bytes, fingerprint, streamhub_wall_seconds(), (1ULL << encoder_t::MAX_FLAGS) - 1);
    if (!saved) { BOOST_LOG(info) << "[StreamHub] persistent-probe=miss reason=record-or-environment"; return false; }
    encoder_t *candidate = nullptr;
    for (auto encoder : encoders) if (encoder->name == saved->encoder && !(encoder->flags & ALWAYS_REPROBE)) candidate = encoder;
    if (!candidate) return false;
    const auto original_h264 = candidate->h264.capabilities;
    const auto original_hevc = candidate->hevc.capabilities;
    const auto original_av1 = candidate->av1.capabilities;
    candidate->h264.capabilities = std::bitset<encoder_t::MAX_FLAGS>(saved->capabilities[0]);
    candidate->hevc.capabilities = std::bitset<encoder_t::MAX_FLAGS>(saved->capabilities[1]);
    candidate->av1.capabilities = std::bitset<encoder_t::MAX_FLAGS>(saved->capabilities[2]);
    auto restore = util::fail_guard([&] {
      candidate->h264.capabilities = original_h264;
      candidate->hevc.capabilities = original_hevc;
      candidate->av1.capabilities = original_av1;
      streamhub_discard_persisted_probe();
    });
    // Matches existing same-target revalidation: a real fresh capture/encode
    // object, conversion and verified first IDR. No deserialized native objects.
    const config_t check {1920, 1080, 60, 6000, 1000, 1, 0, 1, 0, 0, 0, 0};
    std::shared_ptr<platf::display_t> display;
    reset_display(display, candidate->platform_formats->dev_type, output_name, check);
    if (!display || !display->is_codec_supported(candidate->h264.name, check) || validate_config(display, *candidate, check) < 0) {
      BOOST_LOG(info) << "[StreamHub] persistent-probe=failed-real-sample fallback=full";
      return false;
    }
    chosen_encoder = candidate;
    active_hevc_mode = saved->hevc_mode;
    active_av1_mode = saved->av1_mode;
    last_encoder_probe_supported_ref_frames_invalidation = (candidate->flags & REF_FRAMES_INVALIDATION);
    last_encoder_probe_supported_yuv444_for_codec = {
      candidate->h264[encoder_t::PASSED] && candidate->h264[encoder_t::YUV444],
      candidate->hevc[encoder_t::PASSED] && candidate->hevc[encoder_t::YUV444],
      candidate->av1[encoder_t::PASSED] && candidate->av1[encoder_t::YUV444]};
    streamhub_probe_cache.remember(environment);
    streamhub_persisted_ready = true;
    restore.disable();
    BOOST_LOG(info) << "[StreamHub] encoder-probe=persistent-revalidated encoder=" << candidate->name;
    return true;
  }

  static void streamhub_save_persisted_probe(const std::string &environment) {
    if (!chosen_encoder || (chosen_encoder->flags & ALWAYS_REPROBE)) return;
    const auto path = streamhub_persisted_path();
    const auto fingerprint = streamhub_persisted_fingerprint(environment);
    if (fingerprint.empty() || !streamhub_cache_path_allowed(path)) return;
    const streamhub::persisted_probe value {streamhub_wall_seconds(), fingerprint, std::string(chosen_encoder->name),
      {chosen_encoder->h264.capabilities.to_ullong(), chosen_encoder->hevc.capabilities.to_ullong(), chosen_encoder->av1.capabilities.to_ullong()},
      active_hevc_mode, active_av1_mode};
    const auto bytes = streamhub::encode_persisted_probe(value);
    const auto temporary = std::filesystem::path(path).concat(".new." + std::to_string(GetCurrentProcessId()));
    if (!streamhub_cache_path_allowed(temporary)) return;
    const auto file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    const bool complete = WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size() && FlushFileBuffers(file);
    CloseHandle(file);
    if (complete && MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
      streamhub_persisted_ready = true;
      BOOST_LOG(info) << "[StreamHub] persistent-probe=saved";
    } else {
      DeleteFileW(temporary.c_str());
      BOOST_LOG(info) << "[StreamHub] persistent-probe=save-unavailable";
    }
  }
#endif

  const char *persisted_probe_state() {
#ifdef _WIN32
    if (!config::video.streamhub_persist_probe) return "disabled";
    if (!streamhub::probe_plan{config::video.streamhub_probe_plan}.basic_only()) return "not-applicable";
    return streamhub_persisted_ready && !streamhub_encode_failed ? "ready" : "unavailable";
#else
    return "not-applicable";
#endif
  }
