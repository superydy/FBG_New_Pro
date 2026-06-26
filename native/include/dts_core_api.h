#ifndef DTS_CORE_API_H
#define DTS_CORE_API_H

#include <stdint.h>

#ifdef DTS_CORE_EXPORTS
#define DTS_API __declspec(dllexport)
#else
#define DTS_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum DtsCoreState {
    DTS_STATE_UNINITIALIZED = 0,
    DTS_STATE_INITIALIZED = 1,
    DTS_STATE_CONFIGURED = 2,
    DTS_STATE_READY = 3,
    DTS_STATE_RUNNING = 4,
    DTS_STATE_CALIBRATING = 5,
    DTS_STATE_ERROR = 6
} DtsCoreState;

typedef struct DtsSnapshotSummary {
    int64_t timestamp_ms;
    int channel;
    int point_count;
    float min_temp;
    float max_temp;
    float avg_temp;
    float max_pos_m;
    int status_ok;
} DtsSnapshotSummary;

typedef struct DtsWaveDataSummary {
    int64_t timestamp_ms;
    int channel;
    int sensor_count;
    int spectrum_point_count;
    int selected_sensor_index;
    float selected_sensor_position_m;
    float selected_sensor_temperature_c;
    float selected_sensor_wavelength_nm;
} DtsWaveDataSummary;

typedef struct DtsCalibrationSummary {
    int64_t timestamp_ms;
    int channel;
    int sensor_count;
} DtsCalibrationSummary;

typedef struct DtsCalibrationWaveSummary {
    int64_t timestamp_ms;
    int channel;
    int wave_count;
    int sample_count;
} DtsCalibrationWaveSummary;

typedef void* DtsContextHandle;

// lifecycle
DTS_API DtsContextHandle __cdecl DTS_CreateContext();
DTS_API void __cdecl DTS_DestroyContext(DtsContextHandle handle);
DTS_API int __cdecl DTS_Initialize(DtsContextHandle handle, const char* device_ip, int use_1g_init);
DTS_API void __cdecl DTS_Release(DtsContextHandle handle);

// state / error
DTS_API int __cdecl DTS_GetState(DtsContextHandle handle);
DTS_API const char* __cdecl DTS_GetLastError(DtsContextHandle handle);
DTS_API int __cdecl DTS_GetConnect(DtsContextHandle handle);

// config
DTS_API int __cdecl DTS_SetBasicConfig(
    DtsContextHandle handle,
    int start_wl,
    int stop_wl,
    int fiber_length_m,
    float delay_ns,
    int pulse_width,
    int target_profile_points
);

DTS_API int __cdecl DTS_SetWaveConfig(
    DtsContextHandle handle,
    int channel,
    int wave_count,
    const float* center_wavelengths_nm
);

DTS_API int __cdecl DTS_SetChannelConfig(
    DtsContextHandle handle,
    int channel,
    int enabled,
    int fbg_count,
    const int* positions_m,
    const int* wave_indexes,
    float position_scale_to_m
);

DTS_API int __cdecl DTS_SetTemperatureSolveConfig(
    DtsContextHandle handle,
    int channel,
    int sensor_count,
    const float* temp_sensitivity_pm_per_c,
    const float* reference_temperature_c,
    const float* reference_wavelength_nm
);

DTS_API int __cdecl DTS_SetHardwareTuning(
    DtsContextHandle handle,
    int optic_switch_enabled,
    int fiber_density_mode,
    int multi_wave_reverse,
    int edfa_current_ma,
    int edfa_pa_current_ma
);

DTS_API int __cdecl DTS_SetSensorCalibrationConfig(
    DtsContextHandle handle,
    int channel,
    int sensor_count,
    const float* temp_sensitivity_pm_per_c,
    const float* strain_sensitivity,
    const float* reference_temperature_c,
    const float* reference_strain,
    const float* reference_temperature_wavelength_nm,
    const float* reference_strain_wavelength_nm
);

DTS_API int __cdecl DTS_SetSpectrumSensorIndex(
    DtsContextHandle handle,
    int sensor_index
);

DTS_API int __cdecl DTS_SetRunMode(
    DtsContextHandle handle,
    int speed_mode,
    int laser_type,
    int algorithm_type,
    int wavelength_precision_mode,
    int wavelength_average_count,
    int auto_run
);

DTS_API int __cdecl DTS_ApplyHardwareConfig(DtsContextHandle handle);

// calibration
DTS_API int __cdecl DTS_StartCalibration(DtsContextHandle handle, int channel, float threshold);
DTS_API int __cdecl DTS_StopCalibration(DtsContextHandle handle, int channel);
DTS_API int __cdecl DTS_SetAmplifierCurrents(DtsContextHandle handle, int edfa_current_ma, int edfa_pa_current_ma);
DTS_API int __cdecl DTS_GetAmplifierCurrent(DtsContextHandle handle);
DTS_API int __cdecl DTS_RecalculateCalibrationPositions(DtsContextHandle handle, float threshold);

// acquisition
DTS_API int __cdecl DTS_StartAcquisition(DtsContextHandle handle, int channel, int low_speed_all_channels);
DTS_API int __cdecl DTS_StopAcquisition(DtsContextHandle handle, int channel);

// latest snapshot pull mode (for UI timers/pollers)
DTS_API int __cdecl DTS_GetLatestSummary(DtsContextHandle handle, DtsSnapshotSummary* out_summary);
DTS_API int __cdecl DTS_CopyLatestSnapshot(
    DtsContextHandle handle,
    float* positions_m_buffer,
    float* temperatures_c_buffer,
    int max_points,
    DtsSnapshotSummary* out_summary
);

DTS_API int __cdecl DTS_CopyLatestWaveData(
    DtsContextHandle handle,
    float* sensor_positions_m_buffer,
    float* sensor_temperatures_c_buffer,
    float* sensor_wavelengths_nm_buffer,
    int max_sensor_points,
    float* spectrum_x_nm_buffer,
    float* spectrum_y_buffer,
    int max_spectrum_points,
    DtsWaveDataSummary* out_summary
);

DTS_API int __cdecl DTS_CopyLatestCalibrationPositions(
    DtsContextHandle handle,
    int* sensor_positions_buffer,
    int* sensor_wave_indexes_buffer,
    int max_sensor_points,
    DtsCalibrationSummary* out_summary
);

DTS_API int __cdecl DTS_CopyLatestCalibrationWaveData(
    DtsContextHandle handle,
    float* wave_data_buffer,
    int max_values,
    DtsCalibrationWaveSummary* out_summary
);

#ifdef __cplusplus
}
#endif

#endif // DTS_CORE_API_H
