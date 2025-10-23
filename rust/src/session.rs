use scylla::client::session::Session;
use scylla::client::session_builder::SessionBuilder;
use scylla::errors::{NewSessionError, PagerExecutionError, PrepareError};

use crate::CSharpStr;
use crate::ffi::{ArcFFI, BridgedBorrowedSharedPtr, BridgedOwnedSharedPtr, FFI, FromArc};
use crate::prepared_statement::BridgedPreparedStatement;
use crate::row_set::RowSet;
use crate::task::{BridgedFuture, Tcb};

impl FFI for BridgedSession {
    type Origin = FromArc;
}

#[derive(Debug)]
pub struct BridgedSession {
    inner: Session,
}

#[unsafe(no_mangle)]
pub extern "C" fn session_create(tcb: Tcb, uri: CSharpStr<'_>) {
    // Convert the raw C string to a Rust string
    let uri = uri.as_cstr().unwrap().to_str().unwrap();
    let uri = uri.to_owned();

    BridgedFuture::spawn::<_, _, NewSessionError>(tcb, async move {
        println!("Create Session... {}", uri);
        let session = SessionBuilder::new().known_node(&uri).build().await?;
        println!("Session created! {}", uri);
        println!(
            "Contacted node's address: {}",
            session.get_cluster_state().get_nodes_info()[0].address
        );
        Ok(BridgedSession { inner: session })
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn session_free(session_ptr: BridgedOwnedSharedPtr<BridgedSession>) {
    ArcFFI::free(session_ptr);
    println!("Session freed");
}

#[unsafe(no_mangle)]
pub extern "C" fn session_prepare(
    tcb: Tcb,
    session_ptr: BridgedBorrowedSharedPtr<'_, BridgedSession>,
    statement: CSharpStr<'_>,
) {
    // Convert the raw C string to a Rust string.
    let statement = statement.as_cstr().unwrap().to_str().unwrap().to_owned();
    let bridged_session = ArcFFI::cloned_from_ptr(session_ptr).unwrap();

    println!("Scheduling statement for preparation: \"{}\"", statement);

    BridgedFuture::spawn::<_, _, PrepareError>(tcb, async move {
        println!("Preparing statement \"{}\"", statement);
        let ps = bridged_session.inner.prepare(statement).await?;
        println!("Statement prepared");

        Ok(BridgedPreparedStatement { inner: ps })
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn session_query(
    tcb: Tcb,
    session_ptr: BridgedBorrowedSharedPtr<'_, BridgedSession>,
    statement: CSharpStr<'_>,
) {
    // Convert the raw C string to a Rust string.
    let statement = statement.as_cstr().unwrap().to_str().unwrap().to_owned();
    let bridged_session = ArcFFI::cloned_from_ptr(session_ptr).unwrap();

    println!("Scheduling statement for execution: \"{}\"", statement);

    BridgedFuture::spawn::<_, _, PagerExecutionError>(tcb, async move {
        println!("Executing statement \"{}\"", statement);
        let query_pager = bridged_session.inner.query_iter(statement, ()).await?;
        println!("Statement executed");

        Ok(RowSet {
            pager: std::sync::Mutex::new(query_pager),
        })
    })
}
