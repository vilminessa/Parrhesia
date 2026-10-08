/*
 * Parrhesia VST3 Shim — C-API поверх hosting-классов vendor/vst3sdk.
 * Загружает модули VST3, создаёт экземпляры, обрабатывает блоки.
 * Контракт потоков (как у IAudioPlugin): Create/Prepare/GetState/SetState/
 * Destroy — вне аудио-потока; Process — только аудио-поток.
 */
#pragma once

#ifdef PARR_VST3_SHIM_EXPORTS
#define PV3 __declspec(dllexport)
#else
#define PV3 __declspec(dllimport)
#endif

extern "C"
{
    /// Колбэк перечисления классов модуля (category = Audio Module Class).
    typedef void(__cdecl* Pv3EnumCallback)(
        void* context,
        const char* classId,
        const char* name,
        const char* vendor);

    /// Число классов (>=0) или <0 при ошибке (см. Pv3LastError).
    PV3 int __cdecl Pv3Enumerate(const char* modulePath, Pv3EnumCallback callback, void* context);

    /// Текст ошибки последней операции (потокобезопасно на вызывающий поток).
    PV3 const char* __cdecl Pv3LastError();

    /// Создаёт экземпляр (компонент подключён, НЕ активирован). null + ошибка.
    PV3 void* __cdecl Pv3Create(const char* modulePath, const char* classId);

    PV3 void __cdecl Pv3Destroy(void* instance);

    /// setupProcessing + шины + setActive + setProcessing. 0 = успех.
    PV3 int __cdecl Pv3Prepare(void* instance, double sampleRate, int maxBlockFrames, int channels);

    /// Обработка interleaved float32 in-place. 0 = успех.
    PV3 int __cdecl Pv3Process(void* instance, float* interleaved, int frames);

    /// Латентность в кадрах; <0 = ошибка.
    PV3 int __cdecl Pv3GetLatency(void* instance);

    /// Размер состояния (>=0); буфер null/мал — вернуть нужный размер. <0 = ошибка.
    PV3 int __cdecl Pv3GetState(void* instance, unsigned char* buffer, int capacity);

    /// 0 = успех.
    PV3 int __cdecl Pv3SetState(void* instance, const unsigned char* buffer, int length);

    // ===== Параметры (IEditController; main-thread; значения нормированы0..1) =====

    /// Описание параметра VST3 (нормированный диапазон).
    typedef struct Pv3ParamInfo {
        int id;             // Vst::ParamID (unchecked в int)
        int stepCount;      // 0 — непрерывный
        int flags;          // Vst::ParameterInfo::Flags
        double defaultValue;
        double minValue;    // всегда0 (VST3 normalized)
        double maxValue;    // всегда1
        char name[128];     // заголовок (ASCII; нелатиница → '?')
    } Pv3ParamInfo;

    /// Число параметров; <0 = ошибка (нет контроллера).
    PV3 int __cdecl Pv3ParamCount(void* instance);

    /// Описание по индексу. 0 = успех; <0 = ошибка.
    PV3 int __cdecl Pv3GetParamInfo(void* instance, int index, Pv3ParamInfo* out);

    /// Текущее нормированное значение. 0 = успех; <0 = ошибка.
    PV3 int __cdecl Pv3ParamValueGet(void* instance, int id, double* outNormalized);

    /// Установка: setParamNormalized (мгновенно для чтения) + очередь
    /// в ближайший Process (аудио-поток). 0 = успех; <0 = ошибка.
    PV3 int __cdecl Pv3ParamValueSet(void* instance, int id, double normalized);

    // ===== Редактор (IPlugView → дочерний HWND; main-thread) =====

    /// Создаёт view и встраивает в parentHwnd.0 = успех (идемпотентно).
    PV3 int __cdecl Pv3EditorOpen(void* instance, void* parentHwnd);

    /// Размер view в пикселях.0 = успех.
    PV3 int __cdecl Pv3EditorGetSize(void* instance, int* width, int* height);

    /// removed() + release() (идемпотентно).
    PV3 void __cdecl Pv3EditorClose(void* instance);
}
