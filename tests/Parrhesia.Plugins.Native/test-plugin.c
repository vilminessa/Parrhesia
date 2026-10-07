/*
 * Тестовые CLAP-плагины Parrhesia (см. tests/Parrhesia.Plugins.Native/build-test-plugin.bat).
 *
 *   com.parrhesia.test.gain    — удваивает сигнал; state = u32 magic (roundtrip).
 *   com.parrhesia.test.latency — задержка DELAY_FRAMES сэмплов, сообщает
 *                                latency через CLAP_EXT_LATENCY (тест компенсации).
 *
 * Формат буферов CLAP: planar (по указателю на канал), не interleaved —
 * хост конвертирует сам. Лицензия: MIT (как у CLAP headers).
 */

#define _CRT_SECURE_NO_WARNINGS
#include <clap/clap.h>
#include <string.h>
#include <stdlib.h>

#define DELAY_FRAMES 128
#define STATE_MAGIC 0x50525248u /* "PRRH" */

typedef struct {
    int is_latency;
    float d0[DELAY_FRAMES];
    float d1[DELAY_FRAMES];
    unsigned pos;
    uint32_t magic;
} test_state_t;

/* ===== vtable-колбэки (общие для обоих плагинов; различия — по is_latency) ===== */

static test_state_t *state_of(const clap_plugin_t *plugin) {
    return (test_state_t *)plugin->plugin_data;
}

static bool CLAP_ABI plug_init(const clap_plugin_t *plugin) {
    test_state_t *s = state_of(plugin);
    /* ВАЖНО: is_latency задаётся фабрикой ДО init — memset структуры целиком
       обнулял бы его (плагин превращался в gain). Чистим только буферы. */
    memset(s->d0, 0, sizeof(s->d0));
    memset(s->d1, 0, sizeof(s->d1));
    s->pos = 0;
    s->magic = STATE_MAGIC;
    return true;
}

static void CLAP_ABI plug_destroy(const clap_plugin_t *plugin) {
    free(plugin->plugin_data);
}

static bool CLAP_ABI plug_activate(const clap_plugin_t *plugin, double sr, uint32_t min_frames, uint32_t max_frames) {
    (void)plugin;
    (void)sr;
    (void)min_frames;
    (void)max_frames;
    return true;
}

static void CLAP_ABI plug_deactivate(const clap_plugin_t *plugin) { (void)plugin; }

static bool CLAP_ABI plug_start_processing(const clap_plugin_t *plugin) {
    (void)plugin;
    return true;
}

static void CLAP_ABI plug_stop_processing(const clap_plugin_t *plugin) { (void)plugin; }

static void CLAP_ABI plug_reset(const clap_plugin_t *plugin) {
    test_state_t *s = state_of(plugin);
    s->pos = 0;
}

static clap_process_status CLAP_ABI plug_process(const clap_plugin_t *plugin, const clap_process_t *proc) {
    test_state_t *s = state_of(plugin);

    if (proc->audio_inputs_count < 1 || proc->audio_outputs_count < 1) {
        return CLAP_PROCESS_ERROR;
    }

    const clap_audio_buffer_t *in = &proc->audio_inputs[0];
    clap_audio_buffer_t *out = &proc->audio_outputs[0];

    if (in->data32 == NULL || out->data32 == NULL ||
        in->channel_count < 2 || out->channel_count < 2) {
        return CLAP_PROCESS_ERROR;
    }

    const uint32_t frames = proc->frames_count;
    float *in0 = in->data32[0];
    float *in1 = in->data32[1];
    float *out0 = out->data32[0];
    float *out1 = out->data32[1];

    if (s->is_latency) {
        /* Задержка: кольцевый буфер на кадр, чтение входа до записи выхода
           (корректно и при in-place совпадении указателей). */
        for (uint32_t i = 0; i < frames; i++) {
            const float v0 = in0[i];
            const float v1 = in1[i];
            const float d0 = s->d0[s->pos];
            const float d1 = s->d1[s->pos];
            s->d0[s->pos] = v0;
            s->d1[s->pos] = v1;
            out0[i] = d0;
            out1[i] = d1;
            s->pos = (s->pos + 1u) % DELAY_FRAMES;
        }
    } else {
        for (uint32_t i = 0; i < frames; i++) {
            out0[i] = in0[i] * 2.0f;
            out1[i] = in1[i] * 2.0f;
        }
    }

    return CLAP_PROCESS_CONTINUE;
}

/* ===== audio-ports ===== */

static uint32_t CLAP_ABI ports_count(const clap_plugin_t *plugin, bool is_input) {
    (void)plugin;
    (void)is_input;
    return 1;
}

static bool CLAP_ABI ports_get(const clap_plugin_t *plugin, uint32_t index, bool is_input,
                               clap_audio_port_info_t *info) {
    (void)plugin;
    if (index != 0) {
        return false;
    }

    memset(info, 0, sizeof(*info));
    info->id = 1;
    strncpy(info->name, is_input ? "In" : "Out", CLAP_NAME_SIZE - 1);
    info->flags = CLAP_AUDIO_PORT_IS_MAIN;
    info->channel_count = 2;
    info->port_type = NULL;
    info->in_place_pair = CLAP_INVALID_ID;
    return true;
}

static const clap_plugin_audio_ports_t EXT_AUDIO_PORTS = {
    .count = ports_count,
    .get = ports_get,
};

/* ===== latency (только для latency-плагина) ===== */

static uint32_t CLAP_ABI latency_get(const clap_plugin_t *plugin) {
    (void)plugin;
    return DELAY_FRAMES;
}

static const clap_plugin_latency_t EXT_LATENCY = {
    .get = latency_get,
};

/* ===== state: roundtrip u32 magic ===== */

static bool CLAP_ABI state_save(const clap_plugin_t *plugin, const clap_ostream_t *stream) {
    const test_state_t *s = state_of(plugin);
    const int64_t written = stream->write(stream, &s->magic, sizeof(s->magic));
    return written == (int64_t)sizeof(s->magic);
}

static bool CLAP_ABI state_load(const clap_plugin_t *plugin, const clap_istream_t *stream) {
    test_state_t *s = state_of(plugin);
    uint32_t value = 0;
    const int64_t read = stream->read(stream, &value, sizeof(value));
    if (read != (int64_t)sizeof(value)) {
        return false;
    }

    s->magic = value;
    return true;
}

static const clap_plugin_state_t EXT_STATE = {
    .save = state_save,
    .load = state_load,
};

/* ===== get_extension ===== */

static const void *CLAP_ABI plug_get_extension(const clap_plugin_t *plugin, const char *id) {
    if (strcmp(id, CLAP_EXT_AUDIO_PORTS) == 0) {
        return &EXT_AUDIO_PORTS;
    }

    if (strcmp(id, CLAP_EXT_STATE) == 0) {
        return &EXT_STATE;
    }

    test_state_t *s = state_of(plugin);
    if (s->is_latency && strcmp(id, CLAP_EXT_LATENCY) == 0) {
        return &EXT_LATENCY;
    }

    return NULL;
}

static void CLAP_ABI plug_on_main_thread(const clap_plugin_t *plugin) { (void)plugin; }

/* ===== общий vtable ===== */

static const clap_plugin_t PLUGIN_VTABLE = {
    .init = plug_init,
    .destroy = plug_destroy,
    .activate = plug_activate,
    .deactivate = plug_deactivate,
    .start_processing = plug_start_processing,
    .stop_processing = plug_stop_processing,
    .reset = plug_reset,
    .process = plug_process,
    .get_extension = plug_get_extension,
    .on_main_thread = plug_on_main_thread,
};

/* ===== descriptors / factory ===== */

static const clap_plugin_descriptor_t DESC_GAIN = {
    .clap_version = CLAP_VERSION_INIT,
    .id = "com.parrhesia.test.gain",
    .name = "Parrhesia Test Gain",
    .vendor = "Parrhesia",
    .url = "",
    .manual_url = "",
    .support_url = "",
    .version = "1.0.0",
    .description = "Тестовый плагин: удваивает сигнал",
    .features = NULL,
};

static const clap_plugin_descriptor_t DESC_LATENCY = {
    .clap_version = CLAP_VERSION_INIT,
    .id = "com.parrhesia.test.latency",
    .name = "Parrhesia Test Latency",
    .vendor = "Parrhesia",
    .url = "",
    .manual_url = "",
    .support_url = "",
    .version = "1.0.0",
    .description = "Тестовый плагин: задержка128 сэмплов",
    .features = NULL,
};

static uint32_t CLAP_ABI factory_count(const clap_plugin_factory_t *factory) {
    (void)factory;
    return 2;
}

static const clap_plugin_descriptor_t *CLAP_ABI factory_descriptor(const clap_plugin_factory_t *factory,
                                                                   uint32_t index) {
    (void)factory;
    switch (index) {
    case 0:
        return &DESC_GAIN;
    case 1:
        return &DESC_LATENCY;
    default:
        return NULL;
    }
}

static const clap_plugin_t *CLAP_ABI factory_create(const clap_plugin_factory_t *factory,
                                                    const clap_host_t *host,
                                                    const char *plugin_id) {
    (void)factory;
    (void)host;

    int is_latency;
    if (strcmp(plugin_id, DESC_GAIN.id) == 0) {
        is_latency = 0;
    } else if (strcmp(plugin_id, DESC_LATENCY.id) == 0) {
        is_latency = 1;
    } else {
        return NULL;
    }

    test_state_t *s = (test_state_t *)calloc(1, sizeof(test_state_t));
    if (s == NULL) {
        return NULL;
    }

    s->is_latency = is_latency;


    clap_plugin_t *plugin = (clap_plugin_t *)calloc(1, sizeof(clap_plugin_t));
    if (plugin == NULL) {
        free(s);
        return NULL;
    }

    *plugin = PLUGIN_VTABLE;
    plugin->plugin_data = s;
    return plugin;
}

static const clap_plugin_factory_t FACTORY = {
    .get_plugin_count = factory_count,
    .get_plugin_descriptor = factory_descriptor,
    .create_plugin = factory_create,
};

/* ===== entry ===== */

static bool CLAP_ABI entry_init(const char *plugin_path) {
    (void)plugin_path;
    return true;
}

static void CLAP_ABI entry_deinit(void) {}

static const void *CLAP_ABI entry_get_factory(const char *factory_id) {
    if (strcmp(factory_id, CLAP_PLUGIN_FACTORY_ID) == 0) {
        return &FACTORY;
    }

    return NULL;
}

CLAP_EXPORT const clap_plugin_entry_t clap_entry = {
    .clap_version = CLAP_VERSION_INIT,
    .init = entry_init,
    .deinit = entry_deinit,
    .get_factory = entry_get_factory,
};
