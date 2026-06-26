#include "dts_core_api.h"

#include "interface.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <deque>
#include <iomanip>
#include <limits>
#include <mutex>
#include <numeric>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace {

constexpr int kMaxChannels = 32;
constexpr int kOk = 0;
constexpr int kErrInvalidArg = -1;
constexpr int kErrInvalidState = -2;
constexpr int kErrDisconnected = -3;
constexpr int kErrNoData = -4;
constexpr int kDefaultEdfaCurrentMa = 61;
constexpr int kDefaultEdfaPaCurrentMa = 50;
constexpr int64_t kCalibrationWavePreviewMinIntervalMs = 1500;
int64_t NowMs() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

float NarrowToFiniteFloat(double value) {
    if (!std::isfinite(value) ||
        value < static_cast<double>(std::numeric_limits<float>::lowest()) ||
        value > static_cast<double>(std::numeric_limits<float>::max())) {
        return std::numeric_limits<float>::quiet_NaN();
    }

    return static_cast<float>(value);
}

struct WavePacket {
    int channel = 0;
    int64_t ts_ms = 0;
    int spectrum_height = 0;
    std::vector<float> wavelengths_nm;
    std::vector<float> spectrum_x_nm;
    std::vector<float> spectrum_values;
};

struct BasicConfig {
    int start_wl = 1528;
    int stop_wl = 1552;
    int fiber_length_m = 1000;
    float delay_ns = 10.0f;
    int pulse_width = 20;
    int target_profile_points = 5000;
    float reference_temperature_c = 25.0f;
    float wavelength_sensitivity_nm_per_c = 0.01f;
    bool optic_switch_enabled = true;
    int fiber_density_mode = 0;
    bool multi_wave_reverse = false;
    bool auto_run = false;
    int edfa_current_ma = kDefaultEdfaCurrentMa;
    int edfa_pa_current_ma = kDefaultEdfaPaCurrentMa;
};

struct RunConfig {
    int speed_mode = 0;
    int laser_type = 0;
    int algorithm_type = 0;
    int wavelength_precision_mode = 0;
    int wavelength_average_count = 1;
};

struct ChannelConfig {
    bool enabled = false;
    std::vector<int> positions_m;
    std::vector<int> wave_indexes;
    float position_scale_to_m = 1.0f;
    std::vector<float> wave_centers_nm;
    std::vector<float> baseline_wavelengths_nm;
    std::vector<float> temp_sensitivity_pm_per_c;
    std::vector<float> strain_sensitivity;
    std::vector<float> reference_temperature_c;
    std::vector<float> reference_strain;
    std::vector<float> reference_wavelength_nm;
    std::vector<float> reference_strain_wavelength_nm;
};

class DtsContext {
public:
    DtsContext() = default;

    ~DtsContext() {
        Shutdown();
    }

    int Initialize(const char* device_ip, bool use_1g_init) {
        if (device_ip == nullptr || std::strlen(device_ip) == 0) {
            SetError("device_ip is empty");
            return kErrInvalidArg;
        }

        std::lock_guard<std::mutex> lock(mu_);
        if (state_ != DTS_STATE_UNINITIALIZED) {
            SetErrorLocked("Initialize called in invalid state");
            return kErrInvalidState;
        }

        DtsContext* existing = active_ctx_.load();
        if (existing != nullptr && existing != this) {
            SetErrorLocked("Only one active context is supported");
            return kErrInvalidState;
        }

        StartWorkerLocked();

        if (use_1g_init) {
            RCFiberSystem_1G_Init(device_ip);
        } else {
            RCFiberSystem_Init(device_ip);
        }

        RCSystem_FiberCheckDataAquireFun(&DtsContext::OnCheckData, &DtsContext::OnPosInfo);
        RCSystem_FiberFBDataAquireFun(&DtsContext::OnWaveInfo);
        RCSystem_SetAlarmInfo(&DtsContext::OnAlarmInfo);

        active_ctx_.store(this);
        state_ = DTS_STATE_INITIALIZED;
        last_error_.clear();
        return kOk;
    }

    void Shutdown() {
        DtsContext* expected = this;
        active_ctx_.compare_exchange_strong(expected, nullptr);

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ != DTS_STATE_UNINITIALIZED) {
                RCSystem_Release();
                state_ = DTS_STATE_UNINITIALIZED;
            }
        }

        {
            std::lock_guard<std::mutex> qlock(queue_mu_);
            stop_worker_ = true;
            queue_.clear();
        }
        queue_cv_.notify_all();
        if (worker_.joinable()) {
            worker_.join();
        }
    }

    int GetState() const {
        std::lock_guard<std::mutex> lock(mu_);
        return static_cast<int>(state_);
    }

    const char* GetLastError() {
        std::lock_guard<std::mutex> lock(mu_);
        return last_error_.c_str();
    }

    int GetConnect() {
        return RCSystem_GetConnect();
    }

    int SetBasicConfig(
        int start_wl,
        int stop_wl,
        int fiber_length_m,
        float delay_ns,
        int pulse_width,
        int target_profile_points
    ) {
        if (target_profile_points < 16 || fiber_length_m <= 0 || start_wl >= stop_wl) {
            SetError("basic config invalid");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ == DTS_STATE_UNINITIALIZED || state_ == DTS_STATE_RUNNING || state_ == DTS_STATE_CALIBRATING) {
                SetErrorLocked("SetBasicConfig invalid current state");
                return kErrInvalidState;
            }

            basic_.start_wl = start_wl;
            basic_.stop_wl = stop_wl;
            basic_.fiber_length_m = fiber_length_m;
            basic_.delay_ns = delay_ns;
            basic_.pulse_width = pulse_width;
            basic_.target_profile_points = target_profile_points;
        }

        RCSystem_SetDelay(delay_ns);
        RCSystem_Setplusewidth(pulse_width);
        RCSystem_SetParamInfo(start_wl, stop_wl, fiber_length_m);
        RCSystem_SetOpticSwitchEnabled(basic_.optic_switch_enabled ? 1 : 0);
        RCSystem_SetOpticSpecSave(0);
        RCSystem_SetEDFAValue(basic_.edfa_current_ma);
        RCSystem_SetEDFAPAValue(basic_.edfa_pa_current_ma);
        RCSystem_SetGuangQianMidu(basic_.fiber_density_mode, basic_.multi_wave_reverse ? 1 : 0);
        RCSystem_SetHardAutoRun(basic_.auto_run ? 1 : 0);

        return kOk;
    }

    int SetHardwareTuning(
        int optic_switch_enabled,
        int fiber_density_mode,
        int multi_wave_reverse,
        int edfa_current_ma,
        int edfa_pa_current_ma) {
        if ((optic_switch_enabled != 0 && optic_switch_enabled != 1) ||
            fiber_density_mode < 0 ||
            (multi_wave_reverse != 0 && multi_wave_reverse != 1) ||
            edfa_current_ma < 0 ||
            edfa_pa_current_ma < 0) {
            SetError("SetHardwareTuning invalid args");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ == DTS_STATE_UNINITIALIZED || state_ == DTS_STATE_RUNNING || state_ == DTS_STATE_CALIBRATING) {
                SetErrorLocked("SetHardwareTuning invalid current state");
                return kErrInvalidState;
            }

            basic_.optic_switch_enabled = optic_switch_enabled != 0;
            basic_.fiber_density_mode = fiber_density_mode;
            basic_.multi_wave_reverse = multi_wave_reverse != 0;
            basic_.edfa_current_ma = edfa_current_ma;
            basic_.edfa_pa_current_ma = edfa_pa_current_ma;
        }

        RCSystem_SetOpticSwitchEnabled(optic_switch_enabled);
        RCSystem_SetGuangQianMidu(fiber_density_mode, multi_wave_reverse);
        RCSystem_SetEDFAValue(edfa_current_ma);
        RCSystem_SetEDFAPAValue(edfa_pa_current_ma);
        return kOk;
    }

    int SetAmplifierCurrents(int edfa_current_ma, int edfa_pa_current_ma) {
        if (edfa_current_ma < 0 || edfa_pa_current_ma < 0) {
            SetError("SetAmplifierCurrents invalid args");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ == DTS_STATE_UNINITIALIZED) {
                SetErrorLocked("SetAmplifierCurrents invalid current state");
                return kErrInvalidState;
            }
            basic_.edfa_current_ma = edfa_current_ma;
            basic_.edfa_pa_current_ma = edfa_pa_current_ma;
        }

        RCSystem_SetEDFAValue(edfa_current_ma);
        RCSystem_SetEDFAPAValue(edfa_pa_current_ma);
        for (int channel = 0; channel < kMaxChannels; ++channel) {
            RCSystem_SetMutilEDFAValue(edfa_current_ma, channel);
            RCSystem_SetMutilEDFAPAValue(edfa_pa_current_ma, channel);
        }
        RCSystem_SendParamtoHard();
        return kOk;
    }

    int GetAmplifierCurrent() const {
        return RCSystem_GetEDFACurrent();
    }

    int RecalculateCalibrationPositions(float threshold) {
        if (!std::isfinite(threshold) || threshold <= 0.0f) {
            SetError("RecalculateCalibrationPositions invalid threshold");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ != DTS_STATE_CALIBRATING && state_ != DTS_STATE_READY) {
                SetErrorLocked("RecalculateCalibrationPositions invalid current state");
                return kErrInvalidState;
            }
        }

        RCSystem_GetCheckFBFbgPos(threshold);
        return kOk;
    }

    int SetWaveConfig(int channel, int wave_count, const float* center_wavelengths_nm) {
        if (!IsValidChannel(channel) || wave_count < 0 || (wave_count > 0 && center_wavelengths_nm == nullptr)) {
            SetError("SetWaveConfig invalid args");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (!IsConfigurableStateLocked()) {
                SetErrorLocked("SetWaveConfig invalid state");
                return kErrInvalidState;
            }

            ChannelConfig& cfg = channels_[channel];
            cfg.wave_centers_nm.assign(center_wavelengths_nm, center_wavelengths_nm + wave_count);
        }

        RCSystem_SetMutilWaveInfo(channel, wave_count);
        for (int i = 0; i < wave_count; ++i) {
            RCSystem_SetMutilWaveDataInfo(channel, i, center_wavelengths_nm[i]);
        }

        return kOk;
    }

    int SetChannelConfig(int channel, int enabled, int fbg_count, const int* positions_m, const int* wave_indexes, float position_scale_to_m) {
        if (!IsValidChannel(channel) || fbg_count < 0) {
            SetError("SetChannelConfig invalid args");
            return kErrInvalidArg;
        }
        if (fbg_count > 0 && (positions_m == nullptr || wave_indexes == nullptr)) {
            SetError("SetChannelConfig positions or wave indexes missing");
            return kErrInvalidArg;
        }
        if (!std::isfinite(position_scale_to_m) || position_scale_to_m <= 0.0f) {
            SetError("SetChannelConfig position scale invalid");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (!IsConfigurableStateLocked()) {
                SetErrorLocked("SetChannelConfig invalid state");
                return kErrInvalidState;
            }

            ChannelConfig& cfg = channels_[channel];
            cfg.enabled = enabled != 0;
            cfg.positions_m.assign(positions_m, positions_m + fbg_count);
            cfg.wave_indexes.assign(wave_indexes, wave_indexes + fbg_count);
            cfg.position_scale_to_m = position_scale_to_m;
            cfg.baseline_wavelengths_nm.assign(fbg_count, std::numeric_limits<float>::quiet_NaN());

            state_ = DTS_STATE_CONFIGURED;
        }

        RCSystem_SetFBInfo(channel, fbg_count, enabled != 0 ? 1 : 0);
        for (int i = 0; i < fbg_count; ++i) {
            RCSystem_SetFBposInfo(channel, i, positions_m[i], wave_indexes[i]);
        }

        return kOk;
    }

    int SetTemperatureSolveConfig(
        int channel,
        int sensor_count,
        const float* temp_sensitivity_pm_per_c,
        const float* reference_temperature_c,
        const float* reference_wavelength_nm
    ) {
        if (!IsValidChannel(channel) || sensor_count < 0) {
            SetError("SetTemperatureSolveConfig invalid args");
            return kErrInvalidArg;
        }
        if (sensor_count > 0 &&
            (temp_sensitivity_pm_per_c == nullptr || reference_temperature_c == nullptr || reference_wavelength_nm == nullptr)) {
            SetError("SetTemperatureSolveConfig missing arrays");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (!IsConfigurableStateLocked()) {
                SetErrorLocked("SetTemperatureSolveConfig invalid state");
                return kErrInvalidState;
            }

            ChannelConfig& cfg = channels_[channel];
            cfg.temp_sensitivity_pm_per_c.assign(temp_sensitivity_pm_per_c, temp_sensitivity_pm_per_c + sensor_count);
            cfg.reference_temperature_c.assign(reference_temperature_c, reference_temperature_c + sensor_count);
            cfg.reference_wavelength_nm.assign(reference_wavelength_nm, reference_wavelength_nm + sensor_count);
        }

        return kOk;
    }

    int SetSensorCalibrationConfig(
        int channel,
        int sensor_count,
        const float* temp_sensitivity_pm_per_c,
        const float* strain_sensitivity,
        const float* reference_temperature_c,
        const float* reference_strain,
        const float* reference_temperature_wavelength_nm,
        const float* reference_strain_wavelength_nm
    ) {
        if (!IsValidChannel(channel) || sensor_count < 0) {
            SetError("SetSensorCalibrationConfig invalid args");
            return kErrInvalidArg;
        }
        if (sensor_count > 0 &&
            (temp_sensitivity_pm_per_c == nullptr ||
             strain_sensitivity == nullptr ||
             reference_temperature_c == nullptr ||
             reference_strain == nullptr ||
             reference_temperature_wavelength_nm == nullptr ||
             reference_strain_wavelength_nm == nullptr)) {
            SetError("SetSensorCalibrationConfig missing arrays");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (!IsConfigurableStateLocked()) {
                SetErrorLocked("SetSensorCalibrationConfig invalid state");
                return kErrInvalidState;
            }

            ChannelConfig& cfg = channels_[channel];
            if (!cfg.positions_m.empty() && static_cast<int>(cfg.positions_m.size()) != sensor_count) {
                SetErrorLocked("SetSensorCalibrationConfig sensor count mismatch");
                return kErrInvalidArg;
            }

            cfg.temp_sensitivity_pm_per_c.assign(temp_sensitivity_pm_per_c, temp_sensitivity_pm_per_c + sensor_count);
            cfg.strain_sensitivity.assign(strain_sensitivity, strain_sensitivity + sensor_count);
            cfg.reference_temperature_c.assign(reference_temperature_c, reference_temperature_c + sensor_count);
            cfg.reference_strain.assign(reference_strain, reference_strain + sensor_count);
            cfg.reference_wavelength_nm.assign(reference_temperature_wavelength_nm, reference_temperature_wavelength_nm + sensor_count);
            cfg.reference_strain_wavelength_nm.assign(reference_strain_wavelength_nm, reference_strain_wavelength_nm + sensor_count);
        }

        for (int i = 0; i < sensor_count; ++i) {
            RCSystem_SetFBTemYingBianInfo(
                channel,
                i,
                temp_sensitivity_pm_per_c[i],
                strain_sensitivity[i],
                reference_temperature_c[i],
                reference_strain[i],
                reference_temperature_wavelength_nm[i],
                reference_strain_wavelength_nm[i]);
        }

        return kOk;
    }

    int SetSpectrumSensorIndex(int sensor_index) {
        if (sensor_index < 0) {
            SetError("SetSpectrumSensorIndex invalid sensor index");
            return kErrInvalidArg;
        }

        std::lock_guard<std::mutex> lock(mu_);
        selected_spectrum_sensor_index_ = sensor_index;
        return kOk;
    }

    int SetRunMode(
        int speed_mode,
        int laser_type,
        int algorithm_type,
        int wavelength_precision_mode,
        int wavelength_average_count,
        int auto_run) {
        {
            std::lock_guard<std::mutex> lock(mu_);
            if (!IsConfigurableStateLocked()) {
                SetErrorLocked("SetRunMode invalid state");
                return kErrInvalidState;
            }

            run_.speed_mode = speed_mode;
            run_.laser_type = laser_type;
            run_.algorithm_type = algorithm_type;
            run_.wavelength_precision_mode = wavelength_precision_mode;
            run_.wavelength_average_count = std::max(1, wavelength_average_count);
            basic_.auto_run = auto_run != 0;
        }

        RCSystem_SetFBSpeed(speed_mode);
        RCSystem_SetLaserType(laser_type);
        RCSystem_SetAlgothrimType(algorithm_type);
        RCSystem_SetBoChangAlgoJingDu(wavelength_precision_mode);
        RCSystem_SetSoftparam(auto_run != 0 ? 1 : 0, algorithm_type, std::max(1, wavelength_average_count));
        RCSystem_SetHardAutoRun(auto_run != 0 ? 1 : 0);
        return kOk;
    }

    int ApplyHardwareConfig() {
        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ != DTS_STATE_CONFIGURED && state_ != DTS_STATE_READY) {
                SetErrorLocked("ApplyHardwareConfig requires configured state");
                return kErrInvalidState;
            }
        }

        RCSystem_SetHardAutoRun(basic_.auto_run ? 1 : 0);
        for (int channel = 0; channel < kMaxChannels; ++channel) {
            if (!channels_[channel].enabled) {
                continue;
            }

            // The SDK exposes both global and per-channel EDFA setters. In multi-channel
            // operation, keep them synchronized so the active channel does not inherit a
            // stale channel-specific current from a previous vendor-session configuration.
            RCSystem_SetMutilEDFAValue(basic_.edfa_current_ma, channel);
            RCSystem_SetMutilEDFAPAValue(basic_.edfa_pa_current_ma, channel);
        }
        RCSystem_SetSoftparam(basic_.auto_run ? 1 : 0, run_.algorithm_type, std::max(1, run_.wavelength_average_count));
        RCSystem_SendAllFbgInfo();
        RCSystem_SendParamtoHard();

        {
            std::lock_guard<std::mutex> lock(mu_);
            state_ = DTS_STATE_READY;
        }
        return kOk;
    }

    int StartCalibration(int channel, float threshold) {
        if (!IsValidChannel(channel)) {
            SetError("StartCalibration invalid channel");
            return kErrInvalidArg;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ != DTS_STATE_READY) {
                SetErrorLocked("StartCalibration requires ready state");
                return kErrInvalidState;
            }

            latest_calibration_positions_.clear();
            latest_calibration_wave_indexes_.clear();
            latest_calibration_summary_ = {};
            latest_calibration_summary_.channel = channel;
            latest_calibration_wave_values_.clear();
            latest_calibration_wave_summary_ = {};
            latest_calibration_wave_summary_.channel = channel;
            last_calibration_wave_preview_ms_.store(0);
            active_calibration_channel_ = channel;
        }

        RCSystem_StartFBCheck(channel);

        {
            std::lock_guard<std::mutex> lock(mu_);
            state_ = DTS_STATE_CALIBRATING;
        }
        return kOk;
    }

    int StopCalibration(int channel) {
        if (!IsValidChannel(channel)) {
            SetError("StopCalibration invalid channel");
            return kErrInvalidArg;
        }

        RCSystem_StopFBCheck(channel);

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ == DTS_STATE_CALIBRATING) {
                state_ = DTS_STATE_READY;
            }
            if (active_calibration_channel_ == channel) {
                active_calibration_channel_ = -1;
            }
        }

        return kOk;
    }

    int StartAcquisition(int channel, bool low_speed_all_channels) {
        const bool use_all_channels_run = low_speed_all_channels;
        if (!use_all_channels_run && !IsValidChannel(channel)) {
            SetError("StartAcquisition invalid channel");
            return kErrInvalidArg;
        }

        if (RCSystem_GetConnect() != 1) {
            SetError("Device is not connected");
            return kErrDisconnected;
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ != DTS_STATE_READY && state_ != DTS_STATE_RUNNING) {
                SetErrorLocked("StartAcquisition requires ready state");
                return kErrInvalidState;
            }
        }

        if (use_all_channels_run) {
            RCSystem_StartFBLowSpeedAllRun();
        } else {
            RCSystem_StartFBHighSpeedRun(channel);
        }

        {
            std::lock_guard<std::mutex> lock(mu_);
            state_ = DTS_STATE_RUNNING;
            running_channel_ = channel;
            running_all_channels_ = use_all_channels_run;
        }

        return kOk;
    }

    int StopAcquisition(int channel) {
        bool running_all_channels = false;
        {
            std::lock_guard<std::mutex> lock(mu_);
            running_all_channels = running_all_channels_;
        }

        if (!running_all_channels && !IsValidChannel(channel)) {
            SetError("StopAcquisition invalid channel");
            return kErrInvalidArg;
        }

        RCSystem_StopFBHighSpeedRun(running_all_channels ? 0 : channel);

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (state_ == DTS_STATE_RUNNING) {
                state_ = DTS_STATE_READY;
            }
            running_all_channels_ = false;
        }

        return kOk;
    }

    int GetLatestSummary(DtsSnapshotSummary* out_summary) {
        if (out_summary == nullptr) {
            return kErrInvalidArg;
        }

        std::lock_guard<std::mutex> lock(mu_);
        if (!has_snapshot_) {
            return kErrNoData;
        }
        *out_summary = latest_summary_;
        return kOk;
    }

    int CopyLatestSnapshot(
        float* positions_m_buffer,
        float* temperatures_c_buffer,
        int max_points,
        DtsSnapshotSummary* out_summary
    ) {
        if (positions_m_buffer == nullptr || temperatures_c_buffer == nullptr || out_summary == nullptr || max_points <= 0) {
            return kErrInvalidArg;
        }

        std::lock_guard<std::mutex> lock(mu_);
        if (!has_snapshot_) {
            return kErrNoData;
        }

        const int copy_points = std::min(max_points, static_cast<int>(latest_positions_.size()));
        if (copy_points > 0) {
            std::memcpy(positions_m_buffer, latest_positions_.data(), static_cast<size_t>(copy_points) * sizeof(float));
            std::memcpy(temperatures_c_buffer, latest_temperatures_.data(), static_cast<size_t>(copy_points) * sizeof(float));
        }

        *out_summary = latest_summary_;
        out_summary->point_count = copy_points;
        return kOk;
    }

    int CopyLatestWaveData(
        float* sensor_positions_m_buffer,
        float* sensor_temperatures_c_buffer,
        float* sensor_wavelengths_nm_buffer,
        int max_sensor_points,
        float* spectrum_x_nm_buffer,
        float* spectrum_y_buffer,
        int max_spectrum_points,
        DtsWaveDataSummary* out_summary
    ) {
        if (out_summary == nullptr || max_sensor_points <= 0 || max_spectrum_points <= 0) {
            return kErrInvalidArg;
        }

        std::lock_guard<std::mutex> lock(mu_);
        if (!has_snapshot_) {
            return kErrNoData;
        }

        const int copy_sensor_points = std::min(max_sensor_points, static_cast<int>(latest_sensor_positions_.size()));
        if (copy_sensor_points > 0) {
            if (sensor_positions_m_buffer != nullptr) {
                std::memcpy(sensor_positions_m_buffer, latest_sensor_positions_.data(), static_cast<size_t>(copy_sensor_points) * sizeof(float));
            }
            if (sensor_temperatures_c_buffer != nullptr) {
                std::memcpy(sensor_temperatures_c_buffer, latest_sensor_temperatures_.data(), static_cast<size_t>(copy_sensor_points) * sizeof(float));
            }
            if (sensor_wavelengths_nm_buffer != nullptr) {
                std::memcpy(sensor_wavelengths_nm_buffer, latest_sensor_wavelengths_.data(), static_cast<size_t>(copy_sensor_points) * sizeof(float));
            }
        }

        const int copy_spectrum_points = std::min(max_spectrum_points, static_cast<int>(latest_spectrum_x_.size()));
        if (copy_spectrum_points > 0) {
            if (spectrum_x_nm_buffer != nullptr) {
                std::memcpy(spectrum_x_nm_buffer, latest_spectrum_x_.data(), static_cast<size_t>(copy_spectrum_points) * sizeof(float));
            }

            if (spectrum_y_buffer != nullptr &&
                latest_spectrum_height_ > 0 &&
                !latest_spectrum_matrix_.empty()) {
                std::fill_n(spectrum_y_buffer, copy_spectrum_points, std::numeric_limits<float>::quiet_NaN());

                int selected_sensor_index = selected_spectrum_sensor_index_;
                if (selected_sensor_index < 0 || selected_sensor_index >= latest_wave_summary_.sensor_count) {
                    selected_sensor_index =
                        latest_wave_summary_.sensor_count <= 0
                            ? 0
                            : std::clamp(latest_wave_summary_.selected_sensor_index, 0, latest_wave_summary_.sensor_count - 1);
                }

                const size_t spectrum_offset = static_cast<size_t>(selected_sensor_index) * static_cast<size_t>(latest_spectrum_height_);
                const bool row_major_available =
                    spectrum_offset + static_cast<size_t>(copy_spectrum_points) <= latest_spectrum_matrix_.size();
                if (row_major_available) {
                    std::memcpy(
                        spectrum_y_buffer,
                        latest_spectrum_matrix_.data() + spectrum_offset,
                        static_cast<size_t>(copy_spectrum_points) * sizeof(float));
                }
            }
        }

        *out_summary = latest_wave_summary_;
        out_summary->sensor_count = copy_sensor_points;
        out_summary->spectrum_point_count = copy_spectrum_points;
        return kOk;
    }

    int CopyLatestCalibrationPositions(
        int* sensor_positions_buffer,
        int* sensor_wave_indexes_buffer,
        int max_sensor_points,
        DtsCalibrationSummary* out_summary
    ) {
        if (out_summary == nullptr || max_sensor_points <= 0) {
            return kErrInvalidArg;
        }

        std::lock_guard<std::mutex> lock(mu_);
        if (latest_calibration_positions_.empty()) {
            return kErrNoData;
        }

        const int copy_count = std::min(max_sensor_points, static_cast<int>(latest_calibration_positions_.size()));
        if (copy_count > 0 && sensor_positions_buffer != nullptr) {
            std::memcpy(
                sensor_positions_buffer,
                latest_calibration_positions_.data(),
                static_cast<size_t>(copy_count) * sizeof(int));
        }
        if (copy_count > 0 && sensor_wave_indexes_buffer != nullptr) {
            std::memcpy(
                sensor_wave_indexes_buffer,
                latest_calibration_wave_indexes_.data(),
                static_cast<size_t>(copy_count) * sizeof(int));
        }

        *out_summary = latest_calibration_summary_;
        out_summary->sensor_count = copy_count;
        return kOk;
    }

    int CopyLatestCalibrationWaveData(
        float* wave_data_buffer,
        int max_values,
        DtsCalibrationWaveSummary* out_summary
    ) {
        if (out_summary == nullptr || max_values <= 0) {
            return kErrInvalidArg;
        }

        std::lock_guard<std::mutex> lock(mu_);
        if (latest_calibration_wave_values_.empty()) {
            return kErrNoData;
        }

        const int copy_count = std::min(max_values, static_cast<int>(latest_calibration_wave_values_.size()));
        if (copy_count > 0 && wave_data_buffer != nullptr) {
            std::memcpy(
                wave_data_buffer,
                latest_calibration_wave_values_.data(),
                static_cast<size_t>(copy_count) * sizeof(float));
        }

        *out_summary = latest_calibration_wave_summary_;
        out_summary->wave_count = latest_calibration_wave_summary_.wave_count;
        out_summary->sample_count = latest_calibration_wave_summary_.wave_count <= 0
            ? 0
            : copy_count / std::max(1, latest_calibration_wave_summary_.wave_count);
        return kOk;
    }

    static DtsContext* ActiveContext() {
        return active_ctx_.load();
    }

private:
    static void __cdecl OnCheckData(int channel, int wave_count, int sample_count, unsigned long long values_addr) {
        DtsContext* ctx = ActiveContext();
        if (ctx == nullptr) {
            return;
        }

        ctx->OnCalibrationCheckData(channel, wave_count, sample_count, values_addr);
    }

    static void __cdecl OnPosInfo(int channel, int sensor_count, unsigned long long positions_addr) {
        DtsContext* ctx = ActiveContext();
        if (ctx == nullptr) {
            return;
        }

        ctx->OnCalibrationPositions(channel, sensor_count, positions_addr);
    }

    static void __cdecl OnWaveInfo(
        int nchnum,
        int nfbgnum,
        int height,
        unsigned long long dSpectrumX,
        unsigned long long pSpecAllFbgData,
        unsigned long long pWavelength
    ) {
        DtsContext* ctx = ActiveContext();
        if (ctx == nullptr) {
            return;
        }
        ctx->EnqueueWave(nchnum, nfbgnum, height, dSpectrumX, pSpecAllFbgData, pWavelength);
    }

    static void __cdecl OnAlarmInfo(int nCode, const char* info) {
        DtsContext* ctx = ActiveContext();
        if (ctx == nullptr) {
            return;
        }
        ctx->OnAlarmInfoText(nCode, info == nullptr ? "" : info);
    }

    void OnAlarmInfoText(int code, const std::string& info) {
        if (code < 0) {
            std::lock_guard<std::mutex> lock(mu_);
            last_error_ = "SDK alarm: " + info;
            state_ = DTS_STATE_ERROR;
        }
    }

    void OnCalibrationPositions(int channel, int sensor_count, unsigned long long positions_addr) {
        if (!IsValidChannel(channel) || sensor_count <= 0 || positions_addr == 0ULL) {
            return;
        }

        std::vector<int> positions(static_cast<size_t>(sensor_count));
        std::vector<int> wave_indexes(static_cast<size_t>(sensor_count));
        std::memcpy(
            positions.data(),
            reinterpret_cast<const void*>(positions_addr),
            positions.size() * sizeof(int));
        std::memcpy(
            wave_indexes.data(),
            reinterpret_cast<const void*>(positions_addr + static_cast<unsigned long long>(sensor_count) * sizeof(int)),
            wave_indexes.size() * sizeof(int));

        std::lock_guard<std::mutex> lock(mu_);
        latest_calibration_wave_indexes_ = std::move(wave_indexes);
        latest_calibration_positions_ = std::move(positions);
        latest_calibration_summary_.timestamp_ms = NowMs();
        latest_calibration_summary_.channel = channel;
        latest_calibration_summary_.sensor_count = sensor_count;
    }

    void OnCalibrationCheckData(int channel, int wave_count, int sample_count, unsigned long long values_addr) {
        if (!IsValidChannel(channel) || wave_count <= 0 || sample_count <= 0 || values_addr == 0ULL) {
            return;
        }

        const int64_t now_ms = NowMs();
        int64_t previous_ms = last_calibration_wave_preview_ms_.load();
        if (previous_ms > 0 && now_ms - previous_ms < kCalibrationWavePreviewMinIntervalMs) {
            return;
        }
        if (!last_calibration_wave_preview_ms_.compare_exchange_strong(previous_ms, now_ms)) {
            return;
        }

        std::vector<float> values(static_cast<size_t>(wave_count) * static_cast<size_t>(sample_count));
        std::memcpy(
            values.data(),
            reinterpret_cast<const void*>(values_addr),
            values.size() * sizeof(float));

        std::lock_guard<std::mutex> lock(mu_);
        latest_calibration_wave_values_ = std::move(values);
        latest_calibration_wave_summary_.timestamp_ms = now_ms;
        latest_calibration_wave_summary_.channel = channel;
        latest_calibration_wave_summary_.wave_count = wave_count;
        latest_calibration_wave_summary_.sample_count = sample_count;
    }

    static bool IsValidChannel(int channel) {
        return channel >= 0 && channel < kMaxChannels;
    }

    bool IsConfigurableStateLocked() const {
        return state_ == DTS_STATE_INITIALIZED || state_ == DTS_STATE_CONFIGURED || state_ == DTS_STATE_READY;
    }

    void StartWorkerLocked() {
        stop_worker_ = false;
        if (!worker_.joinable()) {
            worker_ = std::thread([this]() { WorkerLoop(); });
        }
    }

    void EnqueueWave(
        int channel,
        int nfbgnum,
        int spectrum_height,
        unsigned long long spectrum_x_addr,
        unsigned long long spectrum_values_addr,
        unsigned long long wavelengths_addr
    ) {
        if (!IsValidChannel(channel) || nfbgnum <= 0 || wavelengths_addr == 0ULL) {
            return;
        }

        // The SDK document describes pWavelength as double[nfbgnum]. The bundled console sample
        // copies it as float[], but that truncates the raw demodulated wavelength stream and forces
        // the UI to fall back to the locally searched peak window.
        std::vector<double> wavelengths_raw(static_cast<size_t>(nfbgnum));
        std::memcpy(
            wavelengths_raw.data(),
            reinterpret_cast<const void*>(wavelengths_addr),
            wavelengths_raw.size() * sizeof(double));

        std::vector<float> wavelengths(static_cast<size_t>(nfbgnum));
        for (size_t i = 0; i < wavelengths_raw.size(); ++i) {
            wavelengths[i] = NarrowToFiniteFloat(wavelengths_raw[i]);
        }

        std::vector<float> spectrum_x;
        std::vector<float> spectrum_values;
        if (spectrum_height > 0 && spectrum_x_addr != 0ULL && spectrum_values_addr != 0ULL) {
            spectrum_x.resize(static_cast<size_t>(spectrum_height));
            std::memcpy(
                spectrum_x.data(),
                reinterpret_cast<const void*>(spectrum_x_addr),
                spectrum_x.size() * sizeof(float));

            const size_t value_count = static_cast<size_t>(nfbgnum) * static_cast<size_t>(spectrum_height);
            spectrum_values.resize(value_count);
            std::memcpy(
                spectrum_values.data(),
                reinterpret_cast<const void*>(spectrum_values_addr),
                value_count * sizeof(float));
        }

        WavePacket packet;
        packet.channel = channel;
        packet.ts_ms = NowMs();
        packet.spectrum_height = spectrum_height;
        packet.wavelengths_nm = std::move(wavelengths);
        packet.spectrum_x_nm = std::move(spectrum_x);
        packet.spectrum_values = std::move(spectrum_values);

        {
            std::lock_guard<std::mutex> lock(queue_mu_);
            if (queue_.size() >= 64) {
                queue_.pop_front();
            }
            queue_.push_back(std::move(packet));
        }
        queue_cv_.notify_one();
    }

    void WorkerLoop() {
        for (;;) {
            WavePacket packet;
            {
                std::unique_lock<std::mutex> lock(queue_mu_);
                queue_cv_.wait(lock, [this]() { return stop_worker_ || !queue_.empty(); });
                if (stop_worker_) {
                    return;
                }
                packet = std::move(queue_.front());
                queue_.pop_front();
            }

            ProcessWavePacket(packet);
        }
    }

    void ProcessWavePacket(const WavePacket& packet) {
        ChannelConfig channel_cfg;

        {
            std::lock_guard<std::mutex> lock(mu_);
            if (!IsValidChannel(packet.channel)) {
                return;
            }
            channel_cfg = channels_[packet.channel];
        }

        if (!channel_cfg.enabled) {
            return;
        }

        std::vector<float> raw_wavelengths = packet.wavelengths_nm;
        const int sensor_count = std::min(static_cast<int>(channel_cfg.positions_m.size()), static_cast<int>(raw_wavelengths.size()));
        if (sensor_count <= 0) {
            return;
        }

        raw_wavelengths.resize(static_cast<size_t>(sensor_count));
        channel_cfg.positions_m.resize(static_cast<size_t>(sensor_count));
        channel_cfg.wave_indexes.resize(static_cast<size_t>(sensor_count));

        const auto resolve_reference_wavelength = [&](int sensor_index) -> float {
            if (sensor_index < static_cast<int>(channel_cfg.reference_wavelength_nm.size())) {
                const float reference = channel_cfg.reference_wavelength_nm[static_cast<size_t>(sensor_index)];
                if (std::isfinite(reference) && reference > 0.0f) {
                    return reference;
                }
            }

            return std::numeric_limits<float>::quiet_NaN();
        };

        const bool has_solve_config =
            channel_cfg.temp_sensitivity_pm_per_c.size() == static_cast<size_t>(sensor_count) &&
            channel_cfg.reference_temperature_c.size() == static_cast<size_t>(sensor_count) &&
            channel_cfg.reference_wavelength_nm.size() == static_cast<size_t>(sensor_count);

        const float position_scale = std::isfinite(channel_cfg.position_scale_to_m) && channel_cfg.position_scale_to_m > 0.0f
            ? channel_cfg.position_scale_to_m
            : 1.0f;

        std::vector<float> sensor_positions(static_cast<size_t>(sensor_count), std::numeric_limits<float>::quiet_NaN());
        std::vector<float> sensor_temps(static_cast<size_t>(sensor_count), std::numeric_limits<float>::quiet_NaN());
        for (int i = 0; i < sensor_count; ++i) {
            const float pos = static_cast<float>(channel_cfg.positions_m[static_cast<size_t>(i)]) * position_scale;
            sensor_positions[static_cast<size_t>(i)] = std::isfinite(pos)
                ? pos
                : std::numeric_limits<float>::quiet_NaN();

            if (!has_solve_config) {
                continue;
            }

            const float wavelength = raw_wavelengths[static_cast<size_t>(i)];
            const float sensitivity_pm = channel_cfg.temp_sensitivity_pm_per_c[static_cast<size_t>(i)];
            const float ref_temp = channel_cfg.reference_temperature_c[static_cast<size_t>(i)];
            const float ref_wavelength = resolve_reference_wavelength(i);
            if (!std::isfinite(wavelength) ||
                !std::isfinite(sensitivity_pm) ||
                std::fabs(sensitivity_pm) <= 0.0001f ||
                !std::isfinite(ref_temp) ||
                !std::isfinite(ref_wavelength)) {
                continue;
            }

            sensor_temps[static_cast<size_t>(i)] = ref_temp + ((wavelength - ref_wavelength) * 1000.0f / sensitivity_pm);
        }

        float min_temp = std::numeric_limits<float>::quiet_NaN();
        float max_temp = std::numeric_limits<float>::quiet_NaN();
        float sum_temp = 0.0f;
        float max_pos = std::numeric_limits<float>::quiet_NaN();
        int valid_temp_count = 0;

        for (int i = 0; i < sensor_count; ++i) {
            const float pos = sensor_positions[static_cast<size_t>(i)];
            const float temp = sensor_temps[static_cast<size_t>(i)];
            if (!std::isfinite(pos) || !std::isfinite(temp)) {
                continue;
            }

            sum_temp += temp;
            if (valid_temp_count == 0 || temp < min_temp) {
                min_temp = temp;
            }
            if (valid_temp_count == 0 || temp > max_temp) {
                max_temp = temp;
                max_pos = pos;
            }
            ++valid_temp_count;
        }

        const float avg_temp = valid_temp_count > 0
            ? (sum_temp / static_cast<float>(valid_temp_count))
            : std::numeric_limits<float>::quiet_NaN();
        const int status_ok = valid_temp_count > 0 ? 1 : 0;
        {
            std::lock_guard<std::mutex> lock(mu_);
            int selected_sensor_index = selected_spectrum_sensor_index_;
            if (selected_sensor_index < 0 || selected_sensor_index >= sensor_count) {
                selected_sensor_index = sensor_count > 0 ? 0 : -1;
            }

            latest_positions_ = sensor_positions;
            latest_temperatures_ = sensor_temps;
            latest_sensor_positions_ = sensor_positions;
            latest_sensor_temperatures_ = sensor_temps;
            latest_sensor_wavelengths_ = raw_wavelengths;
            latest_spectrum_x_ = packet.spectrum_x_nm;
            latest_spectrum_matrix_ = packet.spectrum_values;
            latest_spectrum_height_ = packet.spectrum_height;
            latest_spectrum_sensor_count_ = sensor_count;
            latest_summary_.timestamp_ms = packet.ts_ms;
            latest_summary_.channel = packet.channel;
            latest_summary_.point_count = static_cast<int>(latest_positions_.size());
            latest_summary_.min_temp = min_temp;
            latest_summary_.max_temp = max_temp;
            latest_summary_.avg_temp = avg_temp;
            latest_summary_.max_pos_m = max_pos;
            latest_summary_.status_ok = status_ok;
            latest_wave_summary_.timestamp_ms = packet.ts_ms;
            latest_wave_summary_.channel = packet.channel;
            latest_wave_summary_.sensor_count = sensor_count;
            latest_wave_summary_.spectrum_point_count = static_cast<int>(latest_spectrum_x_.size());
            latest_wave_summary_.selected_sensor_index = selected_sensor_index;
            latest_wave_summary_.selected_sensor_position_m =
                selected_sensor_index >= 0 && selected_sensor_index < static_cast<int>(latest_sensor_positions_.size())
                    ? latest_sensor_positions_[static_cast<size_t>(selected_sensor_index)]
                    : std::numeric_limits<float>::quiet_NaN();
            latest_wave_summary_.selected_sensor_temperature_c =
                selected_sensor_index >= 0 && selected_sensor_index < static_cast<int>(latest_sensor_temperatures_.size())
                    ? latest_sensor_temperatures_[static_cast<size_t>(selected_sensor_index)]
                    : std::numeric_limits<float>::quiet_NaN();
            latest_wave_summary_.selected_sensor_wavelength_nm =
                selected_sensor_index >= 0 && selected_sensor_index < static_cast<int>(latest_sensor_wavelengths_.size())
                    ? latest_sensor_wavelengths_[static_cast<size_t>(selected_sensor_index)]
                    : std::numeric_limits<float>::quiet_NaN();
            has_snapshot_ = true;
        }

    }

    void SetError(const char* message, bool fatal = false) {
        std::lock_guard<std::mutex> lock(mu_);
        SetErrorLocked(message, fatal);
    }

    void SetErrorLocked(const char* message, bool fatal = false) {
        last_error_ = (message == nullptr) ? "unknown error" : message;
        if (fatal && state_ != DTS_STATE_UNINITIALIZED) {
            state_ = DTS_STATE_ERROR;
        }
    }

private:
    mutable std::mutex mu_;
    DtsCoreState state_ = DTS_STATE_UNINITIALIZED;
    std::string last_error_;

    BasicConfig basic_;
    RunConfig run_;
    ChannelConfig channels_[kMaxChannels];

    int running_channel_ = 0;
    bool running_all_channels_ = false;

    std::thread worker_;
    std::mutex queue_mu_;
    std::condition_variable queue_cv_;
    std::deque<WavePacket> queue_;
    bool stop_worker_ = false;

    bool has_snapshot_ = false;
    DtsSnapshotSummary latest_summary_{};
    DtsWaveDataSummary latest_wave_summary_{};
    std::vector<float> latest_positions_;
    std::vector<float> latest_temperatures_;
    std::vector<float> latest_sensor_positions_;
    std::vector<float> latest_sensor_temperatures_;
    std::vector<float> latest_sensor_wavelengths_;
    std::vector<float> latest_spectrum_x_;
    std::vector<float> latest_spectrum_matrix_;
    int latest_spectrum_height_ = 0;
    int latest_spectrum_sensor_count_ = 0;
    std::vector<int> latest_calibration_positions_;
    std::vector<int> latest_calibration_wave_indexes_;
    DtsCalibrationSummary latest_calibration_summary_{};
    std::vector<float> latest_calibration_wave_values_;
    DtsCalibrationWaveSummary latest_calibration_wave_summary_{};
    std::atomic<int64_t> last_calibration_wave_preview_ms_{0};
    int active_calibration_channel_ = -1;
    int selected_spectrum_sensor_index_ = -1;
    static std::atomic<DtsContext*> active_ctx_;
};

std::atomic<DtsContext*> DtsContext::active_ctx_{nullptr};

DtsContext* FromHandle(DtsContextHandle handle) {
    return reinterpret_cast<DtsContext*>(handle);
}

} // namespace

extern "C" {

DtsContextHandle __cdecl DTS_CreateContext() {
    return reinterpret_cast<DtsContextHandle>(new DtsContext());
}

void __cdecl DTS_DestroyContext(DtsContextHandle handle) {
    DtsContext* ctx = FromHandle(handle);
    delete ctx;
}

int __cdecl DTS_Initialize(DtsContextHandle handle, const char* device_ip, int use_1g_init) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->Initialize(device_ip, use_1g_init != 0);
}

void __cdecl DTS_Release(DtsContextHandle handle) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx != nullptr) {
        ctx->Shutdown();
    }
}

int __cdecl DTS_GetState(DtsContextHandle handle) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->GetState();
}

const char* __cdecl DTS_GetLastError(DtsContextHandle handle) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return "invalid handle";
    }
    return ctx->GetLastError();
}

int __cdecl DTS_GetConnect(DtsContextHandle handle) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return 0;
    }
    return ctx->GetConnect();
}

int __cdecl DTS_SetBasicConfig(
    DtsContextHandle handle,
    int start_wl,
    int stop_wl,
    int fiber_length_m,
    float delay_ns,
    int pulse_width,
    int target_profile_points
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetBasicConfig(start_wl, stop_wl, fiber_length_m, delay_ns, pulse_width, target_profile_points);
}

int __cdecl DTS_SetWaveConfig(DtsContextHandle handle, int channel, int wave_count, const float* center_wavelengths_nm) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetWaveConfig(channel, wave_count, center_wavelengths_nm);
}

int __cdecl DTS_SetChannelConfig(
    DtsContextHandle handle,
    int channel,
    int enabled,
    int fbg_count,
    const int* positions_m,
    const int* wave_indexes,
    float position_scale_to_m
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetChannelConfig(channel, enabled, fbg_count, positions_m, wave_indexes, position_scale_to_m);
}

int __cdecl DTS_SetRunMode(
    DtsContextHandle handle,
    int speed_mode,
    int laser_type,
    int algorithm_type,
    int wavelength_precision_mode,
    int wavelength_average_count,
    int auto_run) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetRunMode(speed_mode, laser_type, algorithm_type, wavelength_precision_mode, wavelength_average_count, auto_run);
}

int __cdecl DTS_SetTemperatureSolveConfig(
    DtsContextHandle handle,
    int channel,
    int sensor_count,
    const float* temp_sensitivity_pm_per_c,
    const float* reference_temperature_c,
    const float* reference_wavelength_nm
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetTemperatureSolveConfig(
        channel,
        sensor_count,
        temp_sensitivity_pm_per_c,
        reference_temperature_c,
        reference_wavelength_nm);
}

int __cdecl DTS_SetHardwareTuning(
    DtsContextHandle handle,
    int optic_switch_enabled,
    int fiber_density_mode,
    int multi_wave_reverse,
    int edfa_current_ma,
    int edfa_pa_current_ma
) {
    DtsContext* ctx = static_cast<DtsContext*>(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }

    return ctx->SetHardwareTuning(
        optic_switch_enabled,
        fiber_density_mode,
        multi_wave_reverse,
        edfa_current_ma,
        edfa_pa_current_ma);
}

int __cdecl DTS_SetSensorCalibrationConfig(
    DtsContextHandle handle,
    int channel,
    int sensor_count,
    const float* temp_sensitivity_pm_per_c,
    const float* strain_sensitivity,
    const float* reference_temperature_c,
    const float* reference_strain,
    const float* reference_temperature_wavelength_nm,
    const float* reference_strain_wavelength_nm
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetSensorCalibrationConfig(
        channel,
        sensor_count,
        temp_sensitivity_pm_per_c,
        strain_sensitivity,
        reference_temperature_c,
        reference_strain,
        reference_temperature_wavelength_nm,
        reference_strain_wavelength_nm);
}

int __cdecl DTS_SetSpectrumSensorIndex(DtsContextHandle handle, int sensor_index) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetSpectrumSensorIndex(sensor_index);
}

int __cdecl DTS_ApplyHardwareConfig(DtsContextHandle handle) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->ApplyHardwareConfig();
}

int __cdecl DTS_StartCalibration(DtsContextHandle handle, int channel, float threshold) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->StartCalibration(channel, threshold);
}

int __cdecl DTS_StopCalibration(DtsContextHandle handle, int channel) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->StopCalibration(channel);
}

int __cdecl DTS_SetAmplifierCurrents(DtsContextHandle handle, int edfa_current_ma, int edfa_pa_current_ma) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->SetAmplifierCurrents(edfa_current_ma, edfa_pa_current_ma);
}

int __cdecl DTS_GetAmplifierCurrent(DtsContextHandle handle) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->GetAmplifierCurrent();
}

int __cdecl DTS_RecalculateCalibrationPositions(DtsContextHandle handle, float threshold) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->RecalculateCalibrationPositions(threshold);
}

int __cdecl DTS_CopyLatestCalibrationPositions(
    DtsContextHandle handle,
    int* sensor_positions_buffer,
    int* sensor_wave_indexes_buffer,
    int max_sensor_points,
    DtsCalibrationSummary* out_summary
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }

    return ctx->CopyLatestCalibrationPositions(sensor_positions_buffer, sensor_wave_indexes_buffer, max_sensor_points, out_summary);
}

int __cdecl DTS_CopyLatestCalibrationWaveData(
    DtsContextHandle handle,
    float* wave_data_buffer,
    int max_values,
    DtsCalibrationWaveSummary* out_summary
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }

    return ctx->CopyLatestCalibrationWaveData(wave_data_buffer, max_values, out_summary);
}

int __cdecl DTS_StartAcquisition(DtsContextHandle handle, int channel, int low_speed_all_channels) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->StartAcquisition(channel, low_speed_all_channels != 0);
}

int __cdecl DTS_StopAcquisition(DtsContextHandle handle, int channel) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->StopAcquisition(channel);
}

int __cdecl DTS_GetLatestSummary(DtsContextHandle handle, DtsSnapshotSummary* out_summary) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }
    return ctx->GetLatestSummary(out_summary);
}

int __cdecl DTS_CopyLatestSnapshot(
    DtsContextHandle handle,
    float* positions_m_buffer,
    float* temperatures_c_buffer,
    int max_points,
    DtsSnapshotSummary* out_summary
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }

    return ctx->CopyLatestSnapshot(
        positions_m_buffer,
        temperatures_c_buffer,
        max_points,
        out_summary
    );
}

int __cdecl DTS_CopyLatestWaveData(
    DtsContextHandle handle,
    float* sensor_positions_m_buffer,
    float* sensor_temperatures_c_buffer,
    float* sensor_wavelengths_nm_buffer,
    int max_sensor_points,
    float* spectrum_x_nm_buffer,
    float* spectrum_y_buffer,
    int max_spectrum_points,
    DtsWaveDataSummary* out_summary
) {
    DtsContext* ctx = FromHandle(handle);
    if (ctx == nullptr) {
        return kErrInvalidArg;
    }

    return ctx->CopyLatestWaveData(
        sensor_positions_m_buffer,
        sensor_temperatures_c_buffer,
        sensor_wavelengths_nm_buffer,
        max_sensor_points,
        spectrum_x_nm_buffer,
        spectrum_y_buffer,
        max_spectrum_points,
        out_summary
    );
}

} // extern "C"
