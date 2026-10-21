"""用 Windows Job 对象持有专用 Excel 的身份，父进程退出或工作进程崩溃时清理。"""
from __future__ import annotations

import uuid
import time
import win32job


class ExcelJob:
    def __init__(self) -> None:
        self.name = "Local\\ExcelAutoPDF_Job_" + uuid.uuid4().hex
        self.handle = win32job.CreateJobObject(None, self.name)
        try:
            info = win32job.QueryInformationJobObject(
                self.handle, win32job.JobObjectExtendedLimitInformation)
            info["BasicLimitInformation"]["LimitFlags"] |= win32job.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            win32job.SetInformationJobObject(
                self.handle, win32job.JobObjectExtendedLimitInformation, info)
        except BaseException:
            self.handle.Close()
            raise

    def close(self) -> None:
        if self.handle is not None:
            try:
                win32job.TerminateJobObject(self.handle, 1)
                while win32job.QueryInformationJobObject(
                        self.handle, win32job.JobObjectBasicAccountingInformation)["ActiveProcesses"]:
                    # 任务身份仍由内核对象持有，清理结束前不允许上层报告已停止。
                    time.sleep(0.05)
            finally:
                self.handle.Close()
                self.handle = None


def assign_excel(job_name: str, process_handle) -> None:
    job = win32job.OpenJobObject(win32job.JOB_OBJECT_ASSIGN_PROCESS, False, job_name)
    try:
        win32job.AssignProcessToJobObject(job, process_handle)
    finally:
        job.Close()
