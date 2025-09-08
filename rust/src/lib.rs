use std::ffi::{CStr, c_char, c_void};
use std::future::Future;
use std::marker::PhantomData;
use std::ptr::NonNull;
use std::sync::LazyLock;

use scylla::client::session_builder::SessionBuilder;
use tokio::runtime::Runtime;

static RUNTIME: LazyLock<Runtime> = LazyLock::new(|| Runtime::new().unwrap());

#[repr(transparent)]
#[derive(Clone, Copy)]
pub struct FfiPtr<'a, T: Sized> {
    ptr: Option<NonNull<T>>,
    _phantom: PhantomData<&'a ()>,
}

type CSharpStr<'a> = FfiPtr<'a, c_char>;
impl<'a> CSharpStr<'a> {
    fn as_cstr(&self) -> Option<&CStr> {
        self.ptr.map(|ptr| unsafe { CStr::from_ptr(ptr.as_ptr()) })
    }
}

enum Tcs {}

#[repr(transparent)]
pub struct TcsPtr(FfiPtr<'static, Tcs>);

unsafe impl Send for TcsPtr {}

type CompleteTask = unsafe extern "C" fn(tcs: TcsPtr, result: *mut c_void);

#[unsafe(no_mangle)]
pub extern "C" fn session_create(
    tcs: TcsPtr,
    complete_task: CompleteTask,
    uri: CSharpStr<'_>,
) {
    // Convert the raw C string to a Rust string
    let uri = uri.as_cstr().unwrap().to_str().unwrap();
    let uri = uri.to_owned();

    println!("Hello, World! {}", uri);

    BridgedFuture::spawn(tcs, complete_task, async move {
        println!("Create Session... {}", uri);
        let session = SessionBuilder::new().known_node(&uri).build().await;
        println!("Session created! {}", uri);
        session
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn session_free(
    session_ptr: *mut Session,
) {
    let _session = unsafe {Box::from_raw(session_ptr)};
    println!("Session freed");
    // Dropped here.
}

struct BridgedFuture {}

impl BridgedFuture {
    fn spawn<F, T>(tcs: TcsPtr, complete_task: CompleteTask, future: F)
    where
        F: Future<Output = T> + Send + 'static,
        T: Send + 'static + std::fmt::Debug,
    {
        RUNTIME.spawn(async move {
            let result = future.await;
            // Here you would typically signal the completion to the C# side using `tcs`
            println!(
                "Future completed with result: {:?} - {:?}",
                std::any::type_name::<T>(),
                result
            );
            let boxed_result = Box::new(result);
            let result_ptr = Box::into_raw(boxed_result) as *mut c_void;

            unsafe { complete_task(tcs, result_ptr) };
        });
    }
}
