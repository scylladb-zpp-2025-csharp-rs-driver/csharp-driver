use std::fmt::{Debug, Write};
use std::sync::OnceLock;

use crate::ffi::{FFIBool, FFIStr};
use tracing::Level;
use tracing::field::{Field, Visit};
use tracing::span::Attributes;
use tracing_subscriber::Layer;
use tracing_subscriber::filter::LevelFilter;
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::registry::LookupSpan;

type CSharpLogCallback = unsafe extern "C" fn(level: CsharpLogLevel, message: FFIStr<'_>);

#[derive(Copy, Clone)]
struct ProcessLogger {
    callback: CSharpLogCallback,
    ansi_codes: AnsiCodes,
}

static LOGGER: OnceLock<ProcessLogger> = OnceLock::new();

/// Returns lazily initialized process-global logger configuration.
fn logger() -> Option<&'static ProcessLogger> {
    LOGGER.get()
}

// Initializes the global logger with the provided C# callback.
fn init_logger(callback: CSharpLogCallback, ansi_enabled: bool) {
    LOGGER
        .set(ProcessLogger {
            callback,
            ansi_codes: if ansi_enabled {
                AnsiCodes::ANSI
            } else {
                AnsiCodes::NO_ANSI
            },
        })
        .ok();
}

/// This is equivalent of C# side Diagnostics.CassandraTraceSwitch.Level
/// See LOGGING.md for details.
#[repr(u8)]
pub enum CsharpLogLevel {
    Off = 0,
    Error = 1,
    Warning = 2,
    Info = 3,
    Verbose = 4,
}

impl From<Level> for CsharpLogLevel {
    fn from(level: Level) -> Self {
        match level {
            // We coalesce TRACE and DEBUG into Verbose, since C# doesn't have a separate log level for debug messages.
            Level::TRACE | Level::DEBUG => CsharpLogLevel::Verbose,
            Level::INFO => CsharpLogLevel::Info,
            Level::WARN => CsharpLogLevel::Warning,
            Level::ERROR => CsharpLogLevel::Error,
        }
    }
}

impl From<CsharpLogLevel> for LevelFilter {
    fn from(level: CsharpLogLevel) -> Self {
        match level {
            CsharpLogLevel::Off => LevelFilter::OFF,
            CsharpLogLevel::Error => LevelFilter::ERROR,
            CsharpLogLevel::Warning => LevelFilter::WARN,
            CsharpLogLevel::Info => LevelFilter::INFO,
            CsharpLogLevel::Verbose => LevelFilter::TRACE,
        }
    }
}

// ANSI COLOR CODES
//
// Hardcoded to match the styling that tracing_subscriber's own ANSI formatter
// uses (see `tracing-subscriber`'s `fmt::format::Pretty`/default formatter),
// so Rust logs forwarded to C# keep a familiar look. Note that the log level
// itself (TRACE/DEBUG/INFO/WARN/ERROR) is *not* colored here: that text is
// added on the C# side (see RustBridge.cs/Logger.cs), which is also
// responsible for coloring it.
//
// These are only ever written into a message when ANSI is enabled for the
// process (see `ProcessLogger::ansi_enabled`); otherwise the plain (empty)
// counterparts below are used, so no escape codes are generated at all.

const ANSI_RESET: &str = "\x1b[0m";
const ANSI_BOLD: &str = "\x1b[1m";
const ANSI_DIMMED: &str = "\x1b[2m";
const ANSI_ITALIC: &str = "\x1b[3m";

const NO_ANSI_RESET: &str = "";
const NO_ANSI_BOLD: &str = "";
const NO_ANSI_DIMMED: &str = "";
const NO_ANSI_ITALIC: &str = "";

/// The set of formatting codes to use when building a forwarded log message.
/// Either the real ANSI escape codes, or empty strings when ANSI is disabled.
#[derive(Copy, Clone)]
struct AnsiCodes {
    reset: &'static str,
    bold: &'static str,
    dimmed: &'static str,
    italic: &'static str,
}

impl AnsiCodes {
    const NO_ANSI: Self = Self {
        reset: NO_ANSI_RESET,
        bold: NO_ANSI_BOLD,
        dimmed: NO_ANSI_DIMMED,
        italic: NO_ANSI_ITALIC,
    };

    const ANSI: Self = Self {
        reset: ANSI_RESET,
        bold: ANSI_BOLD,
        dimmed: ANSI_DIMMED,
        italic: ANSI_ITALIC,
    };
}

/// Formats tracing span and event fields for the C# logger.
struct FormattedFieldsVisitor {
    output: String,
    has_entry: bool,
    message_field_name: Option<&'static str>,
}

impl FormattedFieldsVisitor {
    fn new(message_field_name: Option<&'static str>) -> Self {
        Self {
            output: String::new(),
            has_entry: false,
            message_field_name,
        }
    }

    /// Adds one tracing field to the current formatted string.
    fn add_field(&mut self, field: &Field, value: &dyn Debug) {
        let should_omit_name = self
            .message_field_name
            .is_some_and(|message_field_name| field.name() == message_field_name);

        let prefix = if self.has_entry { " " } else { "" };

        if should_omit_name {
            // If this field is the "message" field, we omit the field name and separator to produce cleaner output.
            write!(self.output, "{prefix}{value:?}").unwrap();
        } else {
            let AnsiCodes {
                reset,
                italic,
                dimmed,
                ..
            } = LOGGER.get().map_or(AnsiCodes::NO_ANSI, |l| l.ansi_codes);
            write!(
                self.output,
                "{prefix}{italic}{}{reset}{dimmed}={reset}{:?}",
                field.name(),
                value
            )
            .unwrap();
        }

        self.has_entry = true;
    }
}

impl Visit for FormattedFieldsVisitor {
    fn record_debug(&mut self, field: &Field, value: &dyn Debug) {
        self.add_field(field, value);
    }
}

/// Stores the formatted fields of a tracing span for later reuse by events in the same span.
struct SpanFields(String);

/// tracing_subscriber layer that forwards events to the C# callback.
struct CSharpForwardingLayer;

impl<S> Layer<S> for CSharpForwardingLayer
where
    S: tracing::Subscriber + for<'span> LookupSpan<'span>,
{
    /// Stores the fields attached to a newly created span.
    ///
    /// We do this once so events can reuse the span context later without
    /// reformatting the fields on every log line.
    fn on_new_span(
        &self,
        attrs: &Attributes<'_>,
        id: &tracing::Id,
        ctx: tracing_subscriber::layer::Context<'_, S>,
    ) {
        let Some(span) = ctx.span(id) else {
            return;
        };

        let mut visitor = FormattedFieldsVisitor::new(None);
        attrs.record(&mut visitor);
        span.extensions_mut().insert(SpanFields(visitor.output));
    }

    /// Formats a tracing event and forwards it to the C# callback.
    ///
    /// The message is built from the active span chain plus the event fields,
    /// so the C# side sees the same context that Rust tracing would normally
    /// print in its own output.
    fn on_event(&self, event: &tracing::Event<'_>, ctx: tracing_subscriber::layer::Context<'_, S>) {
        let callback = match logger() {
            Some(logger) => logger.callback,
            None => return, // If the logger is not initialized, we can't forward the log message.
        };

        let meta = event.metadata();
        let event_level = meta.level();

        let codes = LOGGER.get().map_or(AnsiCodes::NO_ANSI, |l| l.ansi_codes);
        let AnsiCodes {
            reset,
            bold,
            dimmed,
            ..
        } = codes;

        let mut visitor = FormattedFieldsVisitor::new(Some("message"));
        event.record(&mut visitor);

        let ffi_level = CsharpLogLevel::from(*event_level);

        let mut prefixed_message = String::new();
        if let Some(scope) = ctx.event_scope(event)
            && let Some(span) = scope.from_root().last()
        {
            write!(prefixed_message, "{bold}{}{reset}", span.name()).unwrap();

            if let Some(fields) = span.extensions().get::<SpanFields>()
                && !fields.0.is_empty()
            {
                write!(
                    prefixed_message,
                    "{bold}{{{reset}{}{bold}}}{reset}",
                    fields.0
                )
                .unwrap();
            }

            write!(prefixed_message, "{dimmed}: {reset}").unwrap();
        }

        write!(prefixed_message, "{dimmed}[{}]{reset} ", meta.target()).unwrap();

        prefixed_message.push_str(&visitor.output);

        unsafe { callback(ffi_level, FFIStr::new(&prefixed_message)) };
    }
}

/// Registers the C# logging callback and initializes the Rust subscriber.
///
/// Must be called at least once before any logging is performed;
/// otherwise, no log output will be produced.
/// Subsequent calls are no-ops.
///
/// `ansi_enabled` controls whether forwarded messages are annotated with ANSI
/// escape codes - should be `true` only for the default Trace-based console/trace-listener path.
#[unsafe(no_mangle)]
pub extern "C" fn configure_rust_logging(
    callback: CSharpLogCallback,
    min_level: CsharpLogLevel,
    ansi_enabled: FFIBool,
) {
    static INIT: std::sync::Once = std::sync::Once::new();
    INIT.call_once(|| {
        init_logger(callback, ansi_enabled.into());

        tracing::subscriber::set_global_default(
            tracing_subscriber::registry()
                .with(LevelFilter::from(min_level))
                .with(CSharpForwardingLayer),
        )
        .expect("failed to set global default subscriber - it might have already been set");
    });
}

// --- TESTING HELPERS ---

/// Emits one log entry at every supported level.
///
/// Used by tests to verify Rust-to-C# log forwarding.
#[cfg(feature = "integration_testing")]
#[unsafe(no_mangle)]
pub extern "C" fn emit_all_log_levels() {
    tracing::trace!("This is a trace message");
    tracing::debug!("This is a debug message");
    tracing::info!("This is an info message");
    tracing::warn!("This is a warning message");
    tracing::error!("This is an error message");
}
