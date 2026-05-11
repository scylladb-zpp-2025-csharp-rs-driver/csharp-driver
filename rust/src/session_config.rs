use std::time::Duration;

use crate::ffi::CSharpStr;

use scylla::{
    client::{execution_profile::ExecutionProfile, session_builder::SessionBuilder},
    policies::load_balancing::{DefaultPolicy, DefaultPolicyBuilder},
};

/// TCP socket options passed from C#.
///
/// Any changes to this struct must be mirrored in the corresponding C# struct.
#[repr(C)]
#[derive(Debug, Copy, Clone)]
pub(crate) struct BridgedTcpConfig {
    /// Whether to enable TCP_NODELAY.
    tcp_nodelay: bool,

    /// Whether to enable TCP keepalive.
    keepalive: bool,

    /// TCP keepalive interval in milliseconds.
    tcp_keepalive_interval_millis: i32,

    /// Receive buffer size in bytes. Values <= 0 mean "use default".
    receive_buffer_size: i32,

    /// Whether to enable SO_REUSEADDR.
    reuse_address: bool,

    /// Send buffer size in bytes. Values <= 0 mean "use default".
    send_buffer_size: i32,

    /// SO_LINGER time in seconds. Values < 0 mean "use default".
    so_linger: i32,
}

impl BridgedTcpConfig {
    /// Apply all TCP socket options in this config to `builder` and return it.
    pub(crate) fn apply_to_builder(self, mut builder: SessionBuilder) -> SessionBuilder {
        builder = builder.tcp_nodelay(self.tcp_nodelay);

        if self.keepalive {
            builder = builder.tcp_keepalive_interval(Duration::from_millis(
                self.tcp_keepalive_interval_millis as u64,
            ));
        }

        if self.receive_buffer_size > 0 {
            builder = builder.tcp_recv_buffer_size(self.receive_buffer_size as usize);
        }

        if self.send_buffer_size > 0 {
            builder = builder.tcp_send_buffer_size(self.send_buffer_size as usize);
        }

        builder = builder.tcp_reuse_address(self.reuse_address);

        if self.so_linger >= 0 {
            builder = builder.tcp_linger(Duration::from_secs(self.so_linger as u64));
        }

        builder
    }
}

#[repr(C)]
pub(crate) struct BridgedLoadBalancingPolicy<'a> {
    pub is_token_aware: bool,
    pub is_dc_aware: bool,
    pub local_dc: CSharpStr<'a>,
}

impl<'a> BridgedLoadBalancingPolicy<'a> {
    pub(crate) fn apply_to_builder(
        self,
        builder: SessionBuilder,
    ) -> (SessionBuilder, Option<DefaultPolicyBuilder>) {
        let local_dc = self
            .local_dc
            .as_cstr()
            .map(|cstr| cstr.to_str().unwrap().to_owned());

        let mut lbpbuilder = DefaultPolicy::builder().token_aware(self.is_token_aware);
        let waiting_for_local_dc = self.is_dc_aware && local_dc.is_none();
        if self.is_dc_aware
            && let Some(local_dc) = local_dc
        {
            lbpbuilder = lbpbuilder.prefer_datacenter(local_dc);
        }
        if waiting_for_local_dc {
            return (builder, Some(lbpbuilder));
        }
        let profile_handle = ExecutionProfile::builder()
            .load_balancing_policy(lbpbuilder.build())
            .build()
            .into_handle();

        (
            builder.default_execution_profile_handle(profile_handle),
            None,
        )
    }
}
/// Configuration for creating a new session passed from C#.
///
/// Any changes to this struct must be mirrored in the corresponding C# struct.
#[repr(C)]
pub(crate) struct BridgedSessionConfig<'a> {
    /// Contact point URIs, comma-separated.
    uri: CSharpStr<'a>,

    /// Keyspace to use, or empty string for none.
    keyspace: CSharpStr<'a>,

    /// Connection timeout in milliseconds.
    connect_timeout_millis: i32,

    /// TCP socket options.
    tcp: BridgedTcpConfig,

    load_balancing_policy: BridgedLoadBalancingPolicy<'a>,
}

impl<'a> BridgedSessionConfig<'a> {
    /// Consume this config and produce a fully-configured [`SessionBuilder`],
    /// returning borrowed URI and keyspace string slices alongside it.
    ///
    /// The returned `&'a str` slices borrow directly from the C#-managed memory;
    /// callers that need the strings to outlive the config's source lifetime must
    /// call `.to_owned()` themselves.
    ///
    /// This is the single place where all session configuration is applied, so
    /// adding new options only requires changes here and in the struct definition.
    pub(crate) fn into_session_builder(
        self,
    ) -> (
        &'a str,
        &'a str,
        SessionBuilder,
        Option<DefaultPolicyBuilder>,
    ) {
        let uri = self.uri.as_cstr().unwrap().to_str().unwrap();
        let keyspace = self.keyspace.as_cstr().unwrap().to_str().unwrap();

        let mut builder = SessionBuilder::new().known_nodes(uri.split(',').map(|s| s.trim()));

        // Rust considers an empty string an invalid keyspace name, while C# treats it
        // as "no keyspace". Setting keyspace via Connect() on the C# side is
        // case-sensitive, so we pass case_sensitive = true here.
        if !keyspace.is_empty() {
            builder = builder.use_keyspace(keyspace, true);
        }

        if self.connect_timeout_millis > 0 {
            builder = builder
                .connection_timeout(Duration::from_millis(self.connect_timeout_millis as u64));
        }

        builder = self.tcp.apply_to_builder(builder);
        let (builder, lbpbuilder) = self.load_balancing_policy.apply_to_builder(builder);

        (uri, keyspace, builder, lbpbuilder)
    }
}
