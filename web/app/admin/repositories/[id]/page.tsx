"use client";

import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useParams, useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Badge } from "@/components/ui/badge";
import {
  ArrowLeft,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Clock3,
  Loader2,
  RefreshCw,
  RotateCcw,
  FileText,
  GitBranch,
  Languages,
  Zap,
  History,
  AlertTriangle,
  Sparkles,
  Pencil,
  Save,
  X,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";
import { useLocale } from "next-intl";
import {
  AdminGraphifyArtifact,
  AdminBranchGenerationTask,
  AdminIncrementalTask,
  AdminRepository,
  AdminRepositoryManagement,
  cancelBranchGenerationTask,
  enqueueBranchFullGeneration,
  generateRepositoryGraphify,
  getIncrementalUpdateTask,
  getRepository,
  getRepositoryGraphifyArtifacts,
  getRepositoryManagement,
  regenerateRepository,
  regenerateRepositoryDocument,
  retryBranchGenerationTask,
  retryIncrementalUpdateTask,
  syncRepositoryStats,
  triggerRepositoryIncrementalUpdate,
  updateRepositoryDocumentContent,
  getRepositoryScanPlan,
  updateRepositoryScanPlan,
  reevaluateRepositoryScanPlan,
  AdminRepositoryScanPlan,
  UpdateRepositoryScanPlanPayload,
} from "@/lib/admin-api";
import { getRepositorySourceTypeLabelKey, isGitRepositorySource } from "@/lib/repository-source";
import { fetchProcessingLogs, fetchRepoDoc, fetchRepoTree } from "@/lib/repository-api";
import type { ProcessingLogResponse, RepoDocResponse, RepoTreeNode } from "@/types/repository";

interface DocOption {
  title: string;
  slug: string;
}

function flattenDocNodes(nodes: RepoTreeNode[]): DocOption[] {
  const docs: DocOption[] = [];
  const walk = (list: RepoTreeNode[]) => {
    list.forEach((node) => {
      if (node.children && node.children.length > 0) {
        walk(node.children);
        return;
      }
      docs.push({ title: node.title, slug: node.slug });
    });
  };
  walk(nodes);
  return docs;
}

function findNodeTrail(nodes: RepoTreeNode[], targetSlug: string, trail: string[] = []): string[] | null {
  for (const node of nodes) {
    const nextTrail = [...trail, node.slug];
    if (node.slug === targetSlug) {
      return nextTrail;
    }
    if (node.children?.length) {
      const result = findNodeTrail(node.children, targetSlug, nextTrail);
      if (result) {
        return result;
      }
    }
  }
  return null;
}

function statusBadgeClass(status: string) {
  const value = status.toLowerCase();
  if (value === "completed" || value === "已完成") return "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200";
  if (value === "processing" || value === "处理中") return "bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200";
  if (value === "pending" || value === "待处理") return "bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-200";
  if (value === "failed" || value === "失败") return "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200";
  if (value === "cancelled" || value === "已取消") return "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200";
  return "bg-muted text-muted-foreground";
}

function mapTaskStatusToAdminTask(
  source: Awaited<ReturnType<typeof getIncrementalUpdateTask>>
): AdminIncrementalTask {
  return {
    taskId: source.taskId,
    branchId: source.branchId,
    branchName: source.branchName,
    status: source.status,
    priority: source.priority,
    isManualTrigger: source.isManualTrigger,
    retryCount: source.retryCount,
    previousCommitId: source.previousCommitId,
    targetCommitId: source.targetCommitId,
    errorMessage: source.errorMessage,
    createdAt: source.createdAt,
    startedAt: source.startedAt,
    completedAt: source.completedAt,
  };
}

function mapBranchGenerationTask(source: AdminBranchGenerationTask): AdminBranchGenerationTask {
  return {
    ...source,
    branchName: source.branchName,
  };
}

function normalizeTaskStatus(status: string) {
  const value = status.toLowerCase();
  if (value.includes("completed") || value.includes("完成")) return "completed";
  if (value.includes("processing") || value.includes("处理")) return "processing";
  if (value.includes("pending") || value.includes("待")) return "pending";
  if (value.includes("failed") || value.includes("失败")) return "failed";
  if (value.includes("cancel") || value.includes("取消")) return "cancelled";
  return "other";
}

export default function AdminRepositoryManagementPage() {
  const router = useRouter();
  const t = useTranslations();
  const locale = useLocale();
  const dateLocale = locale === "zh" ? "zh-CN" : locale;
  const params = useParams<{ id: string }>();
  const repositoryId = useMemo(() => {
    const raw = params?.id;
    if (typeof raw === "string") return raw;
    if (Array.isArray(raw)) return raw[0] ?? "";
    return "";
  }, [params]);

  const [repository, setRepository] = useState<AdminRepository | null>(null);
  const [management, setManagement] = useState<AdminRepositoryManagement | null>(null);
  const [logs, setLogs] = useState<ProcessingLogResponse | null>(null);
  const [doc, setDoc] = useState<RepoDocResponse | null>(null);
  const [graphifyArtifacts, setGraphifyArtifacts] = useState<AdminGraphifyArtifact[]>([]);
  const [docOptions, setDocOptions] = useState<DocOption[]>([]);
  const [docTreeNodes, setDocTreeNodes] = useState<RepoTreeNode[]>([]);
  const [expandedDocSlugs, setExpandedDocSlugs] = useState<Set<string>>(new Set());
  const [isEditingDoc, setIsEditingDoc] = useState(false);
  const [docDraft, setDocDraft] = useState("");
  const [savingDoc, setSavingDoc] = useState(false);

  const [scanPlan, setScanPlan] = useState<AdminRepositoryScanPlan | null>(null);
  const [scanMode, setScanMode] = useState<"Auto" | "Manual">("Auto");
  const [directoryTreeDepthInput, setDirectoryTreeDepthInput] = useState<string>("");
  const [fileListDepthInput, setFileListDepthInput] = useState<string>("");
  const [maxTreeNodesInput, setMaxTreeNodesInput] = useState<string>("");
  const [maxFilesPerDirectoryInput, setMaxFilesPerDirectoryInput] = useState<string>("");
  const [maxTotalFilesInput, setMaxTotalFilesInput] = useState<string>("");
  const [extraExcludedDirsInput, setExtraExcludedDirsInput] = useState<string>("");
  const [savingScanPlan, setSavingScanPlan] = useState(false);
  const [reevaluatingScanPlan, setReevaluatingScanPlan] = useState(false);

  const [selectedBranchId, setSelectedBranchId] = useState("");
  const [selectedLanguage, setSelectedLanguage] = useState("");
  const [selectedDocSlug, setSelectedDocSlug] = useState("");

  const [pageLoading, setPageLoading] = useState(true);
  const [treeLoading, setTreeLoading] = useState(false);
  const [docLoading, setDocLoading] = useState(false);
  const [logsLoading, setLogsLoading] = useState(false);
  const [syncingStats, setSyncingStats] = useState(false);
  const [regeneratingRepo, setRegeneratingRepo] = useState(false);
  const [regeneratingDoc, setRegeneratingDoc] = useState(false);
  const [generatingGraphify, setGeneratingGraphify] = useState(false);
  const [triggeringIncremental, setTriggeringIncremental] = useState(false);
  const [refreshingAll, setRefreshingAll] = useState(false);
  const [taskRefreshingId, setTaskRefreshingId] = useState<string | null>(null);
  const [taskRetryingId, setTaskRetryingId] = useState<string | null>(null);
  const [branchTaskActionId, setBranchTaskActionId] = useState<string | null>(null);
  const [branchTaskCreatingId, setBranchTaskCreatingId] = useState<string | null>(null);
  const [logBranchFilter, setLogBranchFilter] = useState("all");
  const [logTaskFilter, setLogTaskFilter] = useState("");
  const [activeTab, setActiveTab] = useState("docs");
  const graphifyRequestInFlightRef = useRef(false);

  const selectedBranch = useMemo(() => {
    if (!management) return null;
    return management.branches.find((branch) => branch.id === selectedBranchId) ?? null;
  }, [management, selectedBranchId]);

  const selectedLanguageInfo = useMemo(() => {
    if (!selectedBranch) return null;
    return selectedBranch.languages.find((item) => item.languageCode === selectedLanguage) ?? null;
  }, [selectedBranch, selectedLanguage]);

  const branchGenerationTasks = useMemo(
    () => management?.recentBranchGenerationTasks ?? [],
    [management?.recentBranchGenerationTasks]
  );

  const selectedBranchTask = useMemo(() => {
    if (!selectedBranch) return null;
    return branchGenerationTasks.find((task) => task.taskId === selectedBranch.lastGenerationTaskId)
      ?? branchGenerationTasks.find((task) => task.branchId === selectedBranch.id)
      ?? null;
  }, [branchGenerationTasks, selectedBranch]);

  const branchGenerationSummary = useMemo(() => {
    const summary = {
      total: branchGenerationTasks.length,
      active: 0,
      failed: 0,
      completed: 0,
      cancelled: 0,
    };

    branchGenerationTasks.forEach((task) => {
      const status = normalizeTaskStatus(task.status);
      if (status === "pending" || status === "processing") summary.active += 1;
      if (status === "failed") summary.failed += 1;
      if (status === "completed") summary.completed += 1;
      if (status === "cancelled") summary.cancelled += 1;
    });

    return summary;
  }, [branchGenerationTasks]);

  const selectedGraphifyArtifact = useMemo(() => {
    if (!selectedBranch) return null;
    return graphifyArtifacts.find((artifact) => artifact.repositoryBranchId === selectedBranch.id) ?? null;
  }, [graphifyArtifacts, selectedBranch]);

  const isGraphifyArtifactActive = useMemo(() => {
    if (!selectedGraphifyArtifact) return false;
    return ["pending", "processing"].includes(normalizeTaskStatus(selectedGraphifyArtifact.statusName));
  }, [selectedGraphifyArtifact]);

  const isGraphifyGenerating = generatingGraphify || isGraphifyArtifactActive;

  const isDocDirty = useMemo(
    () => isEditingDoc && doc?.exists && docDraft !== (doc.content ?? ""),
    [isEditingDoc, doc, docDraft]
  );

  const isScanPlanDirty = useMemo(() => {
    if (!scanPlan) return false;
    if (scanMode !== scanPlan.mode) return true;
    if (scanMode === "Manual") {
      const extraExcludedStr = (scanPlan.extraExcludedDirs ?? []).join(",");
      const currentExtraExcludedStr = extraExcludedDirsInput
        .split(",")
        .map((s) => s.trim())
        .filter((s) => s.length > 0)
        .join(",");
      if (
        directoryTreeDepthInput !== (scanPlan.directoryTreeDepth?.toString() ?? "") ||
        fileListDepthInput !== (scanPlan.fileListDepth?.toString() ?? "") ||
        maxTreeNodesInput !== (scanPlan.maxTreeNodes?.toString() ?? "") ||
        maxFilesPerDirectoryInput !== (scanPlan.maxFilesPerDirectory?.toString() ?? "") ||
        maxTotalFilesInput !== (scanPlan.maxTotalFiles?.toString() ?? "") ||
        currentExtraExcludedStr !== extraExcludedStr
      ) {
        return true;
      }
    }
    return false;
  }, [
    scanPlan,
    scanMode,
    directoryTreeDepthInput,
    fileListDepthInput,
    maxTreeNodesInput,
    maxFilesPerDirectoryInput,
    maxTotalFilesInput,
    extraExcludedDirsInput,
  ]);

  const confirmDiscardUnsavedChanges = useCallback(() => {
    if (!isDocDirty) return true;
    return window.confirm(t("admin.repositories.management.confirmDiscardUnsaved"));
  }, [isDocDirty, t]);

  const getLocalizedTaskStatus = useCallback(
    (status: string) => {
      switch (normalizeTaskStatus(status)) {
        case "pending":
          return t("admin.repositories.pending");
        case "processing":
          return t("admin.repositories.processing");
        case "completed":
          return t("admin.repositories.completed");
        case "failed":
          return t("admin.repositories.failed");
        case "cancelled":
          return t("admin.repositories.management.status.cancelled");
        default:
          return status;
      }
    },
    [t]
  );

  const branchLanguageMetrics = useMemo(() => {
    if (!management) {
      return {
        branchCount: 0,
        languageCount: 0,
        totalDocuments: 0,
        totalCatalogs: 0,
        avgDocsPerLanguage: 0,
        branchCoverage: 0,
        defaultLanguageCoverage: 0,
      };
    }

    const branchCount = management.branches.length;
    const languageEntries = management.branches.flatMap((branch) => branch.languages);
    const languageCount = new Set(languageEntries.map((item) => item.languageCode)).size;
    const totalDocuments = languageEntries.reduce((sum, item) => sum + item.documentCount, 0);
    const totalCatalogs = languageEntries.reduce((sum, item) => sum + item.catalogCount, 0);
    const defaultLanguageCount = languageEntries.filter((item) => item.isDefault).length;
    const branchesWithDocs = management.branches.filter((branch) =>
      branch.languages.some((item) => item.documentCount > 0)
    ).length;

    return {
      branchCount,
      languageCount,
      totalDocuments,
      totalCatalogs,
      avgDocsPerLanguage:
        languageEntries.length > 0 ? Number((totalDocuments / languageEntries.length).toFixed(1)) : 0,
      branchCoverage: branchCount > 0 ? Math.round((branchesWithDocs / branchCount) * 100) : 0,
      defaultLanguageCoverage:
        languageEntries.length > 0 ? Math.round((defaultLanguageCount / languageEntries.length) * 100) : 0,
    };
  }, [management]);

  const logProgress = useMemo(() => {
    const total = logs?.totalDocuments ?? 0;
    const completed = logs?.completedDocuments ?? 0;
    return {
      total,
      completed,
      percent: total > 0 ? Math.min(100, Math.round((completed / total) * 100)) : 0,
    };
  }, [logs]);

  const selectedLanguageCoverage = useMemo(() => {
    if (!selectedLanguageInfo || selectedLanguageInfo.catalogCount <= 0) return 0;
    return Math.min(
      100,
      Math.round((selectedLanguageInfo.documentCount / selectedLanguageInfo.catalogCount) * 100)
    );
  }, [selectedLanguageInfo]);

  const supportsGitOperations = repository
    ? isGitRepositorySource(repository.sourceType, repository.sourceTypeName)
    : false;

  const processingFlow = useMemo(() => {
    const steps = [
      { index: 0, label: t("admin.repositories.management.steps.prepareWorkspace") },
      { index: 1, label: t("admin.repositories.management.steps.buildCatalog") },
      { index: 2, label: t("admin.repositories.management.steps.generateDocs") },
      { index: 3, label: t("admin.repositories.management.steps.archiveComplete") },
    ];
    const currentStep = logs?.currentStep ?? -1;
    const failed = (logs?.statusName ?? "").toLowerCase() === "failed";

    return steps.map((step) => {
      if (failed && step.index === currentStep) {
        return { ...step, state: "failed" as const };
      }
      if (step.index < currentStep) {
        return { ...step, state: "done" as const };
      }
      if (step.index === currentStep) {
        return { ...step, state: "active" as const };
      }
      return { ...step, state: "pending" as const };
    });
  }, [logs, t]);

  const incrementalSummary = useMemo(() => {
    const summary = {
      total: 0,
      completed: 0,
      processing: 0,
      pending: 0,
      failed: 0,
      cancelled: 0,
      other: 0,
      successRate: 0,
      activeRate: 0,
    };

    if (!management) {
      return summary;
    }

    summary.total = management.recentIncrementalTasks.length;

    management.recentIncrementalTasks.forEach((task) => {
      const normalizedStatus = normalizeTaskStatus(task.status);
      if (normalizedStatus in summary) {
        (summary[normalizedStatus as keyof typeof summary] as number) += 1;
        return;
      }
      summary.other += 1;
    });

    const terminalCount = summary.completed + summary.failed + summary.cancelled;
    summary.successRate = terminalCount > 0 ? Math.round((summary.completed / terminalCount) * 100) : 0;
    summary.activeRate =
      summary.total > 0 ? Math.round(((summary.processing + summary.pending) / summary.total) * 100) : 0;

    return summary;
  }, [management]);

  const incrementalStatusSegments = useMemo(() => {
    const total = incrementalSummary.total || 1;
    return [
      {
        key: "processing",
        label: t("admin.repositories.processing"),
        count: incrementalSummary.processing,
        percent: Math.round((incrementalSummary.processing / total) * 100),
        color: "bg-blue-500/90",
      },
      {
        key: "pending",
        label: t("admin.repositories.pending"),
        count: incrementalSummary.pending,
        percent: Math.round((incrementalSummary.pending / total) * 100),
        color: "bg-slate-400/90",
      },
      {
        key: "completed",
        label: t("admin.repositories.completed"),
        count: incrementalSummary.completed,
        percent: Math.round((incrementalSummary.completed / total) * 100),
        color: "bg-emerald-500/90",
      },
      {
        key: "failed",
        label: t("admin.repositories.failed"),
        count: incrementalSummary.failed,
        percent: Math.round((incrementalSummary.failed / total) * 100),
        color: "bg-red-500/90",
      },
    ];
  }, [incrementalSummary, t]);

  const loadBaseData = useCallback(async () => {
    if (!repositoryId) {
      return;
    }

    setPageLoading(true);
    try {
      const [repoData, managementData, graphifyData, scanPlanData] = await Promise.all([
        getRepository(repositoryId),
        getRepositoryManagement(repositoryId),
        getRepositoryGraphifyArtifacts(repositoryId),
        getRepositoryScanPlan(repositoryId),
      ]);
      setRepository(repoData);
      setManagement(managementData);
      setGraphifyArtifacts(graphifyData);
      setScanPlan(scanPlanData);
    } catch (error) {
      console.error("Failed to load repository management data:", error);
      toast.error(t("admin.repositories.management.toasts.loadManagementFailed"));
    } finally {
      setPageLoading(false);
    }
  }, [repositoryId, t]);

  const loadLogs = useCallback(async (options?: { silent?: boolean }) => {
    if (!repository) return;
    setLogsLoading(true);
    try {
      const branchId = logBranchFilter === "all" ? undefined : logBranchFilter;
      const taskId = logTaskFilter.trim() || undefined;
      const logData = await fetchProcessingLogs(repository.orgName, repository.repoName, undefined, 200, branchId, taskId);
      setLogs(logData);
    } catch (error) {
      console.error("Failed to load logs:", error);
      if (!options?.silent) {
        toast.error(t("admin.repositories.management.toasts.loadLogsFailed"));
      }
    } finally {
      setLogsLoading(false);
    }
  }, [logBranchFilter, logTaskFilter, repository, t]);

  const loadTree = useCallback(async () => {
    if (!repository || !selectedBranch || !selectedLanguage) {
      setDoc(null);
      setDocOptions([]);
      setDocTreeNodes([]);
      setExpandedDocSlugs(new Set());
      return;
    }

    setTreeLoading(true);
    try {
      const treeData = await fetchRepoTree(
        repository.orgName,
        repository.repoName,
        selectedBranch.name,
        selectedLanguage
      );
      const docs = flattenDocNodes(treeData.nodes);
      setDocTreeNodes(treeData.nodes);
      setExpandedDocSlugs(new Set(treeData.nodes.map((node) => node.slug)));
      setDocOptions(docs);
      setSelectedDocSlug((previous) => {
        if (previous && docs.some((item) => item.slug === previous)) {
          return previous;
        }
        if (treeData.defaultSlug && docs.some((item) => item.slug === treeData.defaultSlug)) {
          return treeData.defaultSlug;
        }
        return docs[0]?.slug ?? "";
      });
    } catch (error) {
      console.error("Failed to load repository tree:", error);
      toast.error(t("admin.repositories.management.toasts.loadDocTreeFailed"));
      setDocOptions([]);
      setDocTreeNodes([]);
      setExpandedDocSlugs(new Set());
      setSelectedDocSlug("");
    } finally {
      setTreeLoading(false);
    }
  }, [repository, selectedBranch, selectedLanguage, t]);

  const loadDoc = useCallback(async () => {
    if (!repository || !selectedBranch || !selectedLanguage || !selectedDocSlug) {
      setDoc(null);
      return;
    }

    setDocLoading(true);
    try {
      const docData = await fetchRepoDoc(
        repository.orgName,
        repository.repoName,
        selectedDocSlug,
        selectedBranch.name,
        selectedLanguage
      );
      setDoc(docData);
    } catch (error) {
      console.error("Failed to load document:", error);
      toast.error(t("admin.repositories.management.toasts.loadDocFailed"));
      setDoc(null);
    } finally {
      setDocLoading(false);
    }
  }, [repository, selectedBranch, selectedLanguage, selectedDocSlug, t]);

  const updateTaskInState = useCallback((task: AdminIncrementalTask) => {
    setManagement((previous) => {
      if (!previous) return previous;
      const index = previous.recentIncrementalTasks.findIndex((item) => item.taskId === task.taskId);
      if (index >= 0) {
        const tasks = [...previous.recentIncrementalTasks];
        tasks[index] = task;
        return { ...previous, recentIncrementalTasks: tasks };
      }
      return {
        ...previous,
        recentIncrementalTasks: [task, ...previous.recentIncrementalTasks].slice(0, 20),
      };
    });
  }, []);

  const updateBranchTaskInState = useCallback((task: AdminBranchGenerationTask) => {
    setManagement((previous) => {
      if (!previous) return previous;
      const normalizedTask = mapBranchGenerationTask(task);
      const index = previous.recentBranchGenerationTasks.findIndex((item) => item.taskId === normalizedTask.taskId);
      const tasks = index >= 0
        ? previous.recentBranchGenerationTasks.map((item) =>
            item.taskId === normalizedTask.taskId ? normalizedTask : item
          )
        : [normalizedTask, ...previous.recentBranchGenerationTasks].slice(0, 20);

      const branches = previous.branches.map((branch) =>
        branch.id === normalizedTask.branchId
          ? {
              ...branch,
              generationStatus: normalizedTask.status,
              lastGenerationTaskId: normalizedTask.taskId,
              lastGenerationError: normalizedTask.errorMessage,
              lastGenerationStartedAt: normalizedTask.startedAt,
              lastGenerationCompletedAt: normalizedTask.completedAt,
            }
          : branch
      );

      return {
        ...previous,
        branches,
        recentBranchGenerationTasks: tasks,
      };
    });
  }, []);

  useEffect(() => {
    loadBaseData();
  }, [loadBaseData]);

  useEffect(() => {
    if (!management || management.branches.length === 0) {
      setSelectedBranchId("");
      return;
    }

    setSelectedBranchId((previous) => {
      if (previous && management.branches.some((branch) => branch.id === previous)) {
        return previous;
      }
      return management.branches[0].id;
    });
  }, [management]);

  useEffect(() => {
    if (!selectedBranch) {
      setSelectedLanguage("");
      return;
    }

    setSelectedLanguage((previous) => {
      if (previous && selectedBranch.languages.some((item) => item.languageCode === previous)) {
        return previous;
      }
      const preferred = selectedBranch.languages.find((item) => item.isDefault);
      return preferred?.languageCode ?? selectedBranch.languages[0]?.languageCode ?? "";
    });
  }, [selectedBranch]);

  useEffect(() => {
    if (repository) {
      loadLogs();
    }
  }, [repository, loadLogs]);

  useEffect(() => {
    loadTree();
  }, [loadTree]);

  useEffect(() => {
    loadDoc();
  }, [loadDoc]);

  useEffect(() => {
    if (scanPlan) {
      setScanMode(scanPlan.mode);
      setDirectoryTreeDepthInput(scanPlan.directoryTreeDepth?.toString() ?? "");
      setFileListDepthInput(scanPlan.fileListDepth?.toString() ?? "");
      setMaxTreeNodesInput(scanPlan.maxTreeNodes?.toString() ?? "");
      setMaxFilesPerDirectoryInput(scanPlan.maxFilesPerDirectory?.toString() ?? "");
      setMaxTotalFilesInput(scanPlan.maxTotalFiles?.toString() ?? "");
      setExtraExcludedDirsInput((scanPlan.extraExcludedDirs ?? []).join(", "));
    }
  }, [scanPlan]);

  useEffect(() => {
    if (!isGraphifyArtifactActive || !repositoryId) {
      return;
    }

    const intervalId = window.setInterval(async () => {
      try {
        const artifacts = await getRepositoryGraphifyArtifacts(repositoryId);
        setGraphifyArtifacts(artifacts);
        await loadLogs({ silent: true });
      } catch (error) {
        console.error("Failed to refresh Graphify artifacts:", error);
      }
    }, 10000);

    return () => {
      window.clearInterval(intervalId);
    };
  }, [isGraphifyArtifactActive, repositoryId, loadLogs]);

  useEffect(() => {
    if (branchGenerationSummary.active === 0) {
      return;
    }

    const intervalId = window.setInterval(async () => {
      try {
        await loadBaseData();
        await loadLogs({ silent: true });
      } catch (error) {
        console.error("Failed to refresh branch generation tasks:", error);
      }
    }, 10000);

    return () => {
      window.clearInterval(intervalId);
    };
  }, [branchGenerationSummary.active, loadBaseData, loadLogs]);

  useEffect(() => {
    if (!selectedDocSlug || docTreeNodes.length === 0) return;
    const trail = findNodeTrail(docTreeNodes, selectedDocSlug);
    if (!trail) return;
    setExpandedDocSlugs((previous) => {
      const next = new Set(previous);
      trail.forEach((slug) => next.add(slug));
      return next;
    });
  }, [docTreeNodes, selectedDocSlug]);

  useEffect(() => {
    if (doc?.exists) {
      setDocDraft(doc.content);
    } else {
      setDocDraft("");
    }
    setIsEditingDoc(false);
  }, [doc]);

  const toggleDocExpanded = (slug: string) => {
    setExpandedDocSlugs((previous) => {
      const next = new Set(previous);
      if (next.has(slug)) {
        next.delete(slug);
      } else {
        next.add(slug);
      }
      return next;
    });
  };

  const handleSelectDoc = (slug: string) => {
    if (!confirmDiscardUnsavedChanges()) return;
    setSelectedDocSlug(slug);
  };

  const handleBranchChange = (branchId: string) => {
    if (!confirmDiscardUnsavedChanges()) return;
    setSelectedBranchId(branchId);
  };

  const handleLanguageChange = (languageCode: string) => {
    if (!confirmDiscardUnsavedChanges()) return;
    setSelectedLanguage(languageCode);
  };

  const handleRefreshAll = async () => {
    if (!confirmDiscardUnsavedChanges()) return;
    setRefreshingAll(true);
    try {
      await loadBaseData();
      await loadLogs();
      await loadTree();
    } finally {
      setRefreshingAll(false);
    }
  };

  const handleSyncStats = async () => {
    if (!repositoryId) return;
    if (!supportsGitOperations) {
      toast.warning(t("admin.repositories.syncStatsNotSupported"));
      return;
    }
    setSyncingStats(true);
    try {
      const result = await syncRepositoryStats(repositoryId);
      if (result.success) {
        toast.success(
          t("admin.repositories.management.toasts.syncStatsSuccess", {
            star: result.starCount,
            fork: result.forkCount,
          })
        );
        await loadBaseData();
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.syncStatsFailed"));
      }
    } catch (error) {
      console.error("Failed to sync stats:", error);
      toast.error(t("admin.repositories.management.toasts.syncStatsFailed"));
    } finally {
      setSyncingStats(false);
    }
  };

  const handleRegenerateRepository = async () => {
    if (!repositoryId) return;
    if (isScanPlanDirty) {
      toast.warning(t("admin.repositories.management.scanPlan.saveFirst"));
      return;
    }
    if (!window.confirm(t("admin.repositories.management.confirmRegenerateAll"))) return;

    setRegeneratingRepo(true);
    try {
      const result = await regenerateRepository(repositoryId);
      if (result.success) {
        toast.success(result.message || t("admin.repositories.management.toasts.regenerateAllSuccess"));
        await loadBaseData();
        await loadLogs();
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.regenerateAllFailed"));
      }
    } catch (error) {
      console.error("Failed to regenerate repository:", error);
      toast.error(t("admin.repositories.management.toasts.regenerateAllFailed"));
    } finally {
      setRegeneratingRepo(false);
    }
  };

  const handleBranchFullGeneration = async (branchId: string) => {
    if (!repositoryId) return;
    setBranchTaskCreatingId(branchId);
    try {
      const task = await enqueueBranchFullGeneration(repositoryId, branchId);
      updateBranchTaskInState(task);
      setLogBranchFilter(task.branchId);
      setLogTaskFilter(task.taskId);
      toast.success("Branch full generation queued");
      await loadBaseData();
      await loadLogs({ silent: true });
    } catch (error) {
      console.error("Failed to enqueue branch full generation:", error);
      toast.error(error instanceof Error ? error.message : "Failed to enqueue branch full generation");
    } finally {
      setBranchTaskCreatingId((current) => (current === branchId ? null : current));
    }
  };

  const handleRetryBranchGeneration = async (taskId: string) => {
    setBranchTaskActionId(taskId);
    try {
      const task = await retryBranchGenerationTask(taskId);
      updateBranchTaskInState(task);
      setLogBranchFilter(task.branchId);
      setLogTaskFilter(task.taskId);
      toast.success("Branch generation retry queued");
      await loadBaseData();
      await loadLogs({ silent: true });
    } catch (error) {
      console.error("Failed to retry branch generation:", error);
      toast.error(error instanceof Error ? error.message : "Failed to retry branch generation");
    } finally {
      setBranchTaskActionId((current) => (current === taskId ? null : current));
    }
  };

  const handleCancelBranchGeneration = async (taskId: string) => {
    setBranchTaskActionId(taskId);
    try {
      const task = await cancelBranchGenerationTask(taskId);
      updateBranchTaskInState(task);
      setLogBranchFilter(task.branchId);
      setLogTaskFilter(task.taskId);
      toast.success("Branch generation task cancelled");
      await loadBaseData();
      await loadLogs({ silent: true });
    } catch (error) {
      console.error("Failed to cancel branch generation:", error);
      toast.error(error instanceof Error ? error.message : "Failed to cancel branch generation");
    } finally {
      setBranchTaskActionId((current) => (current === taskId ? null : current));
    }
  };

  const handleViewBranchTaskLogs = (branchId: string, taskId?: string) => {
    setLogBranchFilter(branchId);
    setLogTaskFilter(taskId ?? "");
    setActiveTab("logs");
  };

  const handleGenerateGraphify = async () => {
    if (!repositoryId || !selectedBranch) {
      toast.warning(t("admin.repositories.management.toasts.selectBranchFirst"));
      return;
    }

    if (graphifyRequestInFlightRef.current || isGraphifyGenerating) {
      return;
    }

    graphifyRequestInFlightRef.current = true;
    setGeneratingGraphify(true);
    try {
      const result = await generateRepositoryGraphify(repositoryId, selectedBranch.id);
      if (result.success) {
        toast.success(result.message || t("admin.repositories.management.toasts.graphifyQueued"));
        const artifacts = await getRepositoryGraphifyArtifacts(repositoryId);
        setGraphifyArtifacts(artifacts);
        await loadLogs();
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.graphifyFailed"));
      }
    } catch (error) {
      console.error("Failed to generate Graphify artifacts:", error);
      toast.error(t("admin.repositories.management.toasts.graphifyFailed"));
    } finally {
      graphifyRequestInFlightRef.current = false;
      setGeneratingGraphify(false);
    }
  };

  const handleRegenerateDocument = async () => {
    if (!repositoryId || !selectedBranch || !selectedLanguage || !selectedDocSlug) {
      toast.warning(t("admin.repositories.management.toasts.selectBranchLanguageDocFirst"));
      return;
    }

    if (!confirmDiscardUnsavedChanges()) return;

    if (!window.confirm(t("admin.repositories.management.confirmRegenerateDoc", { doc: selectedDocSlug }))) return;

    setRegeneratingDoc(true);
    try {
      const result = await regenerateRepositoryDocument(repositoryId, {
        branchId: selectedBranch.id,
        languageCode: selectedLanguage,
        documentPath: selectedDocSlug,
      });

      if (result.success) {
        toast.success(result.message || t("admin.repositories.management.toasts.regenerateDocSuccess"));
        await loadDoc();
        await loadLogs();
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.regenerateDocFailed"));
      }
    } catch (error) {
      console.error("Failed to regenerate document:", error);
      toast.error(t("admin.repositories.management.toasts.regenerateDocFailed"));
    } finally {
      setRegeneratingDoc(false);
    }
  };

  const handleStartEditDoc = () => {
    if (!doc?.exists) {
      toast.warning(t("admin.repositories.management.toasts.docNotExistsCannotEdit"));
      return;
    }
    setDocDraft(doc.content);
    setIsEditingDoc(true);
  };

  const handleCancelEditDoc = () => {
    if (isDocDirty && !window.confirm(t("admin.repositories.management.confirmDiscardEdit"))) {
      return;
    }
    setDocDraft(doc?.content ?? "");
    setIsEditingDoc(false);
  };

  const handleSaveDocContent = async () => {
    if (!repositoryId || !selectedBranch || !selectedLanguage || !selectedDocSlug) {
      toast.warning(t("admin.repositories.management.toasts.selectBranchLanguageDocFirst"));
      return;
    }

    setSavingDoc(true);
    try {
      const result = await updateRepositoryDocumentContent(repositoryId, {
        branchId: selectedBranch.id,
        languageCode: selectedLanguage,
        documentPath: selectedDocSlug,
        content: docDraft,
      });

      if (result.success) {
        toast.success(result.message || t("admin.repositories.management.toasts.saveDocSuccess"));
        setDoc((previous) =>
          previous
            ? {
                ...previous,
                content: docDraft,
              }
            : previous
        );
        setIsEditingDoc(false);
        await loadLogs();
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.saveDocFailed"));
      }
    } catch (error) {
      console.error("Failed to update document content:", error);
      toast.error(t("admin.repositories.management.toasts.saveDocFailed"));
    } finally {
      setSavingDoc(false);
    }
  };

  const handleTriggerIncremental = async () => {
    if (!repositoryId || !selectedBranch) {
      toast.warning(t("admin.repositories.management.toasts.selectBranchFirst"));
      return;
    }
    if (!supportsGitOperations) {
      toast.warning(t("admin.repositories.management.incrementalNotSupported"));
      return;
    }
    if (isScanPlanDirty) {
      toast.warning(t("admin.repositories.management.scanPlan.saveFirst"));
      return;
    }

    setTriggeringIncremental(true);
    try {
      const result = await triggerRepositoryIncrementalUpdate(repositoryId, selectedBranch.id);
      if (result.success) {
        toast.success(t("admin.repositories.management.toasts.triggerIncrementalSuccess", { taskId: result.taskId }));
        await loadBaseData();
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.triggerIncrementalFailed"));
      }
    } catch (error) {
      console.error("Failed to trigger incremental update:", error);
      toast.error(t("admin.repositories.management.toasts.triggerIncrementalFailed"));
    } finally {
      setTriggeringIncremental(false);
    }
  };

  const handleSaveScanPlan = async () => {
    if (!repositoryId) return;

    const payload: UpdateRepositoryScanPlanPayload = {
      mode: scanMode,
    };

    if (scanMode === "Manual") {
      const dirTreeDepth = parseInt(directoryTreeDepthInput, 10);
      const fileDepth = parseInt(fileListDepthInput, 10);
      const treeNodes = parseInt(maxTreeNodesInput, 10);
      const filesPerDir = parseInt(maxFilesPerDirectoryInput, 10);
      const totalFiles = parseInt(maxTotalFilesInput, 10);

      if (
        isNaN(dirTreeDepth) ||
        isNaN(fileDepth) ||
        isNaN(treeNodes) ||
        isNaN(filesPerDir) ||
        isNaN(totalFiles)
      ) {
        toast.error(t("admin.repositories.management.scanPlan.invalidNumber"));
        return;
      }

      if (dirTreeDepth < 0 || fileDepth < 0 || treeNodes < 0 || filesPerDir < 0 || totalFiles < 0) {
        toast.error(t("admin.repositories.management.scanPlan.minLimit"));
        return;
      }

      payload.directoryTreeDepth = dirTreeDepth;
      payload.fileListDepth = fileDepth;
      payload.maxTreeNodes = treeNodes;
      payload.maxFilesPerDirectory = filesPerDir;
      payload.maxTotalFiles = totalFiles;
      payload.extraExcludedDirs = extraExcludedDirsInput
        .split(",")
        .map((s) => s.trim())
        .filter((s) => s.length > 0);
    }

    setSavingScanPlan(true);
    try {
      const updatedPlan = await updateRepositoryScanPlan(repositoryId, payload);
      setScanPlan(updatedPlan);
      toast.success(t("admin.repositories.management.scanPlan.saveSuccess"));
    } catch (error) {
      console.error("Failed to save scan plan:", error);
      toast.error(t("admin.repositories.management.toasts.saveScanPlanFailed"));
    } finally {
      setSavingScanPlan(false);
    }
  };

  const handleReevaluateScanPlan = async () => {
    if (!repositoryId) return;

    setReevaluatingScanPlan(true);
    try {
      const result = await reevaluateRepositoryScanPlan(repositoryId);
      if (result.success) {
        setScanPlan(result.data);
        toast.success(result.message || t("admin.repositories.management.scanPlan.reevaluateSuccess"));
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.reevaluateScanPlanFailed"));
        if (result.data) {
          setScanPlan(result.data);
        }
      }
    } catch (error) {
      console.error("Failed to reevaluate scan plan:", error);
      toast.error(t("admin.repositories.management.toasts.reevaluateScanPlanFailed"));
    } finally {
      setReevaluatingScanPlan(false);
    }
  };

  const handleRefreshTask = async (taskId: string) => {
    setTaskRefreshingId(taskId);
    try {
      const taskStatus = await getIncrementalUpdateTask(taskId);
      if (taskStatus.success) {
        updateTaskInState(mapTaskStatusToAdminTask(taskStatus));
      } else {
        toast.error(t("admin.repositories.management.toasts.refreshTaskFailed"));
      }
    } catch (error) {
      console.error("Failed to refresh task:", error);
      toast.error(t("admin.repositories.management.toasts.refreshTaskFailed"));
    } finally {
      setTaskRefreshingId(null);
    }
  };

  const handleRetryTask = async (taskId: string) => {
    setTaskRetryingId(taskId);
    try {
      const result = await retryIncrementalUpdateTask(taskId);
      if (result.success) {
        toast.success(result.message || t("admin.repositories.management.toasts.retryTaskSuccess"));
        await handleRefreshTask(taskId);
      } else {
        toast.error(result.message || t("admin.repositories.management.toasts.retryTaskFailed"));
      }
    } catch (error) {
      console.error("Failed to retry task:", error);
      toast.error(t("admin.repositories.management.toasts.retryTaskFailed"));
    } finally {
      setTaskRetryingId(null);
    }
  };

  const renderDocTreeNodes = (nodes: RepoTreeNode[], depth = 0): React.ReactNode => {
    return nodes.map((node, index) => {
      const hasChildren = node.children.length > 0;
      const isExpanded = expandedDocSlugs.has(node.slug);
      const isActive = node.slug === selectedDocSlug;

      return (
        <div key={`${node.slug}-${depth}`} className="space-y-1">
          <div
            className={`flex items-center gap-1 rounded px-1 py-1 transition-all duration-200 ${
              isActive ? "bg-primary/10 text-primary ring-1 ring-primary/30" : "hover:bg-muted"
            }`}
            style={{ paddingLeft: depth * 14 + 4, animationDelay: `${Math.min(index * 16, 140)}ms` }}
          >
            <button
              type="button"
              className={`flex h-5 w-5 items-center justify-center rounded transition-colors ${
                hasChildren ? "hover:bg-muted-foreground/10" : "opacity-40"
              }`}
              onClick={() => {
                if (!hasChildren) return;
                toggleDocExpanded(node.slug);
              }}
            >
              {hasChildren ? (
                isExpanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />
              ) : (
                <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/60" />
              )}
            </button>
            <button
              type="button"
              className={`flex-1 truncate text-left text-sm ${hasChildren ? "cursor-pointer" : ""}`}
              onClick={() => {
                if (hasChildren) {
                  toggleDocExpanded(node.slug);
                } else {
                  handleSelectDoc(node.slug);
                }
              }}
              title={node.slug}
            >
              {node.title}
            </button>
          </div>

          {hasChildren && isExpanded && (
            <div className="animate-in fade-in-0 slide-in-from-top-1 duration-200">
              {renderDocTreeNodes(node.children, depth + 1)}
            </div>
          )}
        </div>
      );
    });
  };

  if (pageLoading) {
    return (
      <div className="flex h-[60vh] items-center justify-center animate-in fade-in-0 duration-300">
        <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    );
  }

  if (!repository || !management) {
    return (
      <div className="space-y-4 animate-in fade-in-0 slide-in-from-bottom-2 duration-300">
        <Button
          variant="outline"
          onClick={() => router.push("/admin/repositories")}
          className="transition-all duration-200 hover:-translate-y-0.5"
        >
          <ArrowLeft className="mr-2 h-4 w-4" />
          {t("admin.repositories.management.backToList")}
        </Button>
        <Card className="p-6 text-center text-muted-foreground">{t("admin.repositories.management.notFound")}</Card>
      </div>
    );
  }

  return (
    <div className="space-y-6 animate-in fade-in-0 duration-500">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="space-y-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => router.push("/admin/repositories")}
            className="transition-all duration-200 hover:-translate-x-1"
          >
            <ArrowLeft className="mr-2 h-4 w-4" />
            {t("admin.repositories.management.backToList")}
          </Button>
          <h1 className="text-2xl font-bold">{repository.orgName}/{repository.repoName}</h1>
          <div className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
            <span className="inline-flex rounded-full bg-secondary px-2 py-1 text-xs">
              {t(`admin.repositories.${getRepositorySourceTypeLabelKey(repository.sourceType, repository.sourceTypeName)}`)}
            </span>
            <span className="break-all">{repository.sourceLocation || repository.gitUrl}</span>
          </div>
          <p className="inline-flex items-center gap-2 text-xs text-muted-foreground">
            <Sparkles className="h-3.5 w-3.5 text-primary" />
            {t("admin.repositories.management.visualHint")}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={handleRefreshAll} disabled={refreshingAll}>
            <RefreshCw className={`mr-2 h-4 w-4 ${refreshingAll ? "animate-spin" : ""}`} />
            {t("admin.repositories.management.refresh")}
          </Button>
          <Button variant="outline" onClick={handleSyncStats} disabled={syncingStats || !supportsGitOperations} title={supportsGitOperations ? t("admin.repositories.management.syncStats") : t("admin.repositories.syncStatsNotSupported")}>
            {syncingStats ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RotateCcw className="mr-2 h-4 w-4" />}
            {t("admin.repositories.management.syncStats")}
          </Button>
          <Button variant="outline" onClick={handleTriggerIncremental} disabled={triggeringIncremental || !selectedBranch || !supportsGitOperations} title={supportsGitOperations ? t("admin.repositories.management.triggerIncremental") : t("admin.repositories.management.incrementalNotSupported")}>
            {triggeringIncremental ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Zap className="mr-2 h-4 w-4" />}
            {t("admin.repositories.management.triggerIncremental")}
          </Button>
          <Button variant="outline" onClick={handleGenerateGraphify} disabled={isGraphifyGenerating || !selectedBranch}>
            {isGraphifyGenerating ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Sparkles className="mr-2 h-4 w-4" />}
            {t("admin.repositories.management.generateGraphify")}
          </Button>
          <Button variant="destructive" onClick={handleRegenerateRepository} disabled={regeneratingRepo}>
            {regeneratingRepo ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RefreshCw className="mr-2 h-4 w-4" />}
            {t("admin.repositories.management.regenerateAll")}
          </Button>
        </div>
      </div>

      <Card className="p-4 transition-all duration-300 hover:shadow-sm">
        <div className="grid gap-4 xl:grid-cols-[1.2fr_1fr]">
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <p className="text-sm font-semibold">{t("admin.repositories.management.summaryTitle")}</p>
              <span className={`inline-flex rounded px-2 py-1 text-xs ${statusBadgeClass(repository.statusText)}`}>
                {getLocalizedTaskStatus(repository.statusText)}
              </span>
            </div>
            <div className="rounded-lg border bg-muted/30 p-3">
              <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                <span>{t("admin.repositories.management.branchCoverage")}</span>
                <span>{branchLanguageMetrics.branchCoverage}%</span>
              </div>
              <Progress value={branchLanguageMetrics.branchCoverage} className="h-2.5" />
            </div>
            <div className="rounded-lg border bg-muted/30 p-3">
              <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                <span>{t("admin.repositories.management.defaultLanguageCoverage")}</span>
                <span>{branchLanguageMetrics.defaultLanguageCoverage}%</span>
              </div>
              <Progress value={branchLanguageMetrics.defaultLanguageCoverage} className="h-2.5" />
            </div>
          </div>

          <div className="grid gap-3 sm:grid-cols-2">
            <Card className="p-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-sm">
              <p className="text-xs text-muted-foreground">{t("admin.repositories.management.branchLanguage")}</p>
              <p className="text-xl font-semibold">
                {branchLanguageMetrics.branchCount} / {branchLanguageMetrics.languageCount}
              </p>
            </Card>
            <Card className="p-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-sm">
              <p className="text-xs text-muted-foreground">{t("admin.repositories.management.docCatalog")}</p>
              <p className="text-xl font-semibold">
                {branchLanguageMetrics.totalDocuments} / {branchLanguageMetrics.totalCatalogs}
              </p>
            </Card>
            <Card className="p-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-sm">
              <p className="text-xs text-muted-foreground">Star / Fork</p>
              <p className="text-xl font-semibold">
                {supportsGitOperations
                  ? `${repository.starCount} / ${repository.forkCount}`
                  : t("admin.repositories.management.notAvailable")}
              </p>
            </Card>
            <Card className="p-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-sm">
              <p className="text-xs text-muted-foreground">{t("admin.repositories.management.avgDocsPerLanguage")}</p>
              <p className="text-xl font-semibold">{branchLanguageMetrics.avgDocsPerLanguage}</p>
            </Card>
            <Card className="p-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-sm">
              <p className="text-xs text-muted-foreground">{t("admin.repositories.management.graphifyStatus")}</p>
              <p className="text-xl font-semibold">
                {selectedGraphifyArtifact
                  ? getLocalizedTaskStatus(selectedGraphifyArtifact.statusName)
                  : t("admin.repositories.management.notGenerated")}
              </p>
              {selectedGraphifyArtifact?.completedAt && (
                <p className="mt-1 text-xs text-muted-foreground">
                  {new Date(selectedGraphifyArtifact.completedAt).toLocaleString(dateLocale)}
                </p>
              )}
            </Card>
            <Card className="p-3 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-sm">
              <p className="text-xs text-muted-foreground">Branch generation</p>
              <p className="text-xl font-semibold">
                {selectedBranchTask ? getLocalizedTaskStatus(selectedBranchTask.status) : "Idle"}
              </p>
              {selectedBranchTask?.taskId && (
                <p className="mt-1 truncate font-mono text-xs text-muted-foreground" title={selectedBranchTask.taskId}>
                  {selectedBranchTask.taskId}
                </p>
              )}
              {selectedBranchTask?.errorMessage && (
                <p className="mt-1 line-clamp-2 text-xs text-red-500" title={selectedBranchTask.errorMessage}>
                  {selectedBranchTask.errorMessage}
                </p>
              )}
            </Card>
          </div>
        </div>
      </Card>

      <Card className="p-4 transition-all duration-300 hover:shadow-sm">
        <div className="space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-3">
            <div className="space-y-1">
              <h2 className="text-lg font-semibold flex items-center gap-2">
                <Sparkles className="h-5 w-5 text-primary animate-pulse" />
                {t("admin.repositories.management.scanPlan.title")}
              </h2>
              <p className="text-xs text-muted-foreground">
                {t("admin.repositories.management.scanPlan.hint")}
              </p>
            </div>
            <div className="flex items-center gap-2">
              <Select value={scanMode} onValueChange={(val) => setScanMode(val as "Auto" | "Manual")}>
                <SelectTrigger className="w-[180px]">
                  <SelectValue placeholder={t("admin.repositories.management.scanPlan.mode")} />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="Auto">{t("admin.repositories.management.scanPlan.modeAuto")}</SelectItem>
                  <SelectItem value="Manual">{t("admin.repositories.management.scanPlan.modeManual")}</SelectItem>
                </SelectContent>
              </Select>
              {scanMode === "Auto" ? (
                <>
                  {isScanPlanDirty && (
                    <Button onClick={handleSaveScanPlan} disabled={savingScanPlan}>
                      {savingScanPlan ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Save className="mr-2 h-4 w-4" />}
                      {t("admin.repositories.management.scanPlan.save")}
                    </Button>
                  )}
                  <Button variant="outline" onClick={handleReevaluateScanPlan} disabled={reevaluatingScanPlan || isScanPlanDirty}>
                    {reevaluatingScanPlan ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RefreshCw className="mr-2 h-4 w-4" />}
                    {t("admin.repositories.management.scanPlan.reevaluate")}
                  </Button>
                </>
              ) : (
                <Button onClick={handleSaveScanPlan} disabled={savingScanPlan || !isScanPlanDirty}>
                  {savingScanPlan ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Save className="mr-2 h-4 w-4" />}
                  {t("admin.repositories.management.scanPlan.save")}
                </Button>
              )}
            </div>
          </div>

          <div className="grid gap-6 md:grid-cols-2">
            {/* Display/Edit Scan plan parameters */}
            <div className="space-y-4 rounded-lg border bg-muted/10 p-4">
              <h3 className="text-sm font-semibold text-muted-foreground border-b pb-2">
                {scanMode === "Manual"
                  ? t("admin.repositories.management.scanPlan.manualConfig")
                  : t("admin.repositories.management.scanPlan.autoPlanTitle")}
              </h3>

              <div className="grid gap-3 sm:grid-cols-2">
                <div className="space-y-1.5">
                  <label className="text-xs font-medium text-muted-foreground">
                    {t("admin.repositories.management.scanPlan.directoryTreeDepth")}
                  </label>
                  <Input
                    type="number"
                    value={directoryTreeDepthInput}
                    onChange={(e) => setDirectoryTreeDepthInput(e.target.value)}
                    disabled={scanMode === "Auto"}
                    min="0"
                  />
                </div>
                <div className="space-y-1.5">
                  <label className="text-xs font-medium text-muted-foreground">
                    {t("admin.repositories.management.scanPlan.fileListDepth")}
                  </label>
                  <Input
                    type="number"
                    value={fileListDepthInput}
                    onChange={(e) => setFileListDepthInput(e.target.value)}
                    disabled={scanMode === "Auto"}
                    min="0"
                  />
                </div>
                <div className="space-y-1.5">
                  <label className="text-xs font-medium text-muted-foreground">
                    {t("admin.repositories.management.scanPlan.maxTreeNodes")}
                  </label>
                  <Input
                    type="number"
                    value={maxTreeNodesInput}
                    onChange={(e) => setMaxTreeNodesInput(e.target.value)}
                    disabled={scanMode === "Auto"}
                    min="0"
                  />
                </div>
                <div className="space-y-1.5">
                  <label className="text-xs font-medium text-muted-foreground">
                    {t("admin.repositories.management.scanPlan.maxFilesPerDirectory")}
                  </label>
                  <Input
                    type="number"
                    value={maxFilesPerDirectoryInput}
                    onChange={(e) => setMaxFilesPerDirectoryInput(e.target.value)}
                    disabled={scanMode === "Auto"}
                    min="0"
                  />
                </div>
                <div className="space-y-1.5 sm:col-span-2">
                  <label className="text-xs font-medium text-muted-foreground">
                    {t("admin.repositories.management.scanPlan.maxTotalFiles")}
                  </label>
                  <Input
                    type="number"
                    value={maxTotalFilesInput}
                    onChange={(e) => setMaxTotalFilesInput(e.target.value)}
                    disabled={scanMode === "Auto"}
                    min="0"
                  />
                </div>
                <div className="space-y-1.5 sm:col-span-2">
                  <label className="text-xs font-medium text-muted-foreground">
                    {t("admin.repositories.management.scanPlan.extraExcludedDirs")}
                  </label>
                  <Input
                    type="text"
                    value={extraExcludedDirsInput}
                    onChange={(e) => setExtraExcludedDirsInput(e.target.value)}
                    disabled={scanMode === "Auto"}
                    placeholder="e.g. build, temp, dist"
                  />
                </div>
              </div>
            </div>

            {/* Resolved plan status metadata */}
            <div className="space-y-4 rounded-lg border bg-muted/10 p-4">
              <h3 className="text-sm font-semibold text-muted-foreground border-b pb-2">
                {t("admin.repositories.management.scanPlan.resolvedPlan")}
              </h3>
              {scanPlan ? (
                <div className="space-y-3 text-sm">
                  <div className="flex items-center justify-between border-b border-muted py-1">
                    <span className="text-muted-foreground">{t("admin.repositories.management.scanPlan.source")}</span>
                    <Badge variant={scanPlan.source === "Database" ? "secondary" : "outline"}>
                      {scanPlan.source}
                    </Badge>
                  </div>
                  <div className="flex items-center justify-between border-b border-muted py-1">
                    <span className="text-muted-foreground">{t("admin.repositories.management.scanPlan.confidence")}</span>
                    <span className="font-medium">
                      {scanPlan.confidence !== undefined && scanPlan.confidence !== null
                        ? `${(scanPlan.confidence * 100).toFixed(0)}%`
                        : "N/A"}
                    </span>
                  </div>
                  <div className="flex items-center justify-between border-b border-muted py-1">
                    <span className="text-muted-foreground">{t("admin.repositories.management.scanPlan.updatedAt")}</span>
                    <span className="font-mono text-xs">
                      {scanPlan.updatedAt ? new Date(scanPlan.updatedAt).toLocaleString(dateLocale) : "N/A"}
                    </span>
                  </div>
                  <div className="space-y-1.5 pt-1">
                    <span className="text-xs font-medium text-muted-foreground">{t("admin.repositories.management.scanPlan.reason")}</span>
                    <p className="rounded border bg-muted/30 p-2.5 text-xs text-muted-foreground whitespace-pre-wrap leading-relaxed">
                      {scanPlan.reason || "N/A"}
                    </p>
                  </div>
                </div>
              ) : (
                <div className="flex h-[200px] items-center justify-center text-muted-foreground text-xs">
                  {t("admin.repositories.management.scanPlan.noPlan")}
                </div>
              )}
            </div>
          </div>
        </div>
      </Card>

      <Tabs value={activeTab} onValueChange={setActiveTab} className="w-full">
        <TabsList className="grid w-full grid-cols-2 gap-2 md:grid-cols-4">
          <TabsTrigger value="branches" className="transition-all data-[state=active]:shadow-sm">
            <GitBranch className="mr-2 h-4 w-4" />
            {t("admin.repositories.management.tabs.branches")}
            <span className="ml-1 rounded bg-muted px-1.5 text-[10px] leading-4">
              {management.branches.length}
            </span>
          </TabsTrigger>
          <TabsTrigger value="docs" className="transition-all data-[state=active]:shadow-sm">
            <FileText className="mr-2 h-4 w-4" />
            {t("admin.repositories.management.tabs.docs")}
            <span className="ml-1 rounded bg-muted px-1.5 text-[10px] leading-4">{docOptions.length}</span>
          </TabsTrigger>
          <TabsTrigger value="logs" className="transition-all data-[state=active]:shadow-sm">
            <History className="mr-2 h-4 w-4" />
            {t("admin.repositories.management.tabs.logs")}
            <span className="ml-1 rounded bg-muted px-1.5 text-[10px] leading-4">{logs?.logs.length ?? 0}</span>
          </TabsTrigger>
          <TabsTrigger value="incremental" className="transition-all data-[state=active]:shadow-sm">
            <Zap className="mr-2 h-4 w-4" />
            {t("admin.repositories.management.tabs.incremental")}
            <span className="ml-1 rounded bg-muted px-1.5 text-[10px] leading-4">
              {management.recentIncrementalTasks.length}
            </span>
          </TabsTrigger>
        </TabsList>

        <TabsContent value="branches" className="mt-4 animate-in fade-in-0 slide-in-from-bottom-2 duration-300">
          <div className="grid gap-4 lg:grid-cols-2">
            {management.branches.map((branch, index) => {
              const isSelected = selectedBranchId === branch.id;
              const branchStatus = branch.generationStatus ?? "Idle";
              const normalizedStatus = normalizeTaskStatus(branchStatus);
              const isBranchActive = normalizedStatus === "pending" || normalizedStatus === "processing";
              const isBranchFailed = normalizedStatus === "failed";
              const isBranchCancelled = normalizedStatus === "cancelled";
              const canRetryBranch = (isBranchFailed || isBranchCancelled) && Boolean(branch.lastGenerationTaskId);
              const canCancelBranch = normalizedStatus === "pending" && Boolean(branch.lastGenerationTaskId);
              const actionBusy = branchTaskCreatingId === branch.id || branchTaskActionId === branch.lastGenerationTaskId;
              return (
                <div
                  key={branch.id}
                  className="text-left"
                >
                  <Card
                    className={`p-4 animate-in fade-in-0 slide-in-from-bottom-1 transition-all duration-300 hover:-translate-y-0.5 hover:shadow-sm ${
                      isSelected ? "ring-1 ring-primary/40 bg-primary/5" : ""
                    }`}
                    style={{ animationDelay: `${Math.min(index * 35, 180)}ms` }}
                  >
                    <div className="mb-3 flex items-center justify-between">
                      <button
                        type="button"
                        className="font-semibold text-left hover:text-primary hover:underline underline-offset-4"
                        onClick={() => handleBranchChange(branch.id)}
                      >
                        {branch.name}
                      </button>
                      <div className="flex flex-wrap items-center justify-end gap-2">
                        <Badge variant={isBranchFailed ? "destructive" : isBranchActive ? "secondary" : "outline"}>
                          {isBranchActive && <Loader2 className="mr-1 h-3 w-3 animate-spin" />}
                          {branchStatus}
                        </Badge>
                        {isSelected && <Badge>{t("admin.repositories.management.branchSelected")}</Badge>}
                      </div>
                    </div>
                    <div className="space-y-2 text-sm">
                      <p className="text-muted-foreground">
                        {t("admin.repositories.management.lastCommit")}: {branch.lastCommitId || t("admin.repositories.management.notAvailable")}
                      </p>
                      <p className="text-muted-foreground">
                        {t("admin.repositories.management.lastProcessed")}:{" "}
                        {branch.lastProcessedAt ? new Date(branch.lastProcessedAt).toLocaleString(dateLocale) : t("admin.repositories.management.notAvailable")}
                      </p>
                      {branch.lastGenerationTaskId && (
                        <p className="text-muted-foreground">
                          Branch task: <span className="font-mono text-xs">{branch.lastGenerationTaskId}</span>
                        </p>
                      )}
                      {branch.lastGenerationError && (
                        <p className="rounded border border-red-500/30 bg-red-500/10 p-2 text-xs text-red-600">
                          {branch.lastGenerationError}
                        </p>
                      )}
                    </div>
                    <div className="mt-3 flex flex-wrap gap-2">
                      {branch.languages.map((language) => (
                        <Badge key={language.id} variant={language.isDefault ? "default" : "secondary"}>
                          <Languages className="mr-1 h-3 w-3" />
                          {language.languageCode}
                          <span className="ml-1 text-[10px] opacity-80">
                            ({language.documentCount}/{language.catalogCount})
                          </span>
                        </Badge>
                      ))}
                    </div>
                    <div className="mt-4 flex flex-wrap gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => handleBranchFullGeneration(branch.id)}
                        disabled={actionBusy || isBranchActive}
                      >
                        {branchTaskCreatingId === branch.id ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RefreshCw className="mr-2 h-4 w-4" />}
                        Branch full
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => branch.lastGenerationTaskId && handleRetryBranchGeneration(branch.lastGenerationTaskId)}
                        disabled={actionBusy || !canRetryBranch}
                      >
                        {branchTaskActionId === branch.lastGenerationTaskId && canRetryBranch ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RotateCcw className="mr-2 h-4 w-4" />}
                        Retry
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        onClick={() => branch.lastGenerationTaskId && handleCancelBranchGeneration(branch.lastGenerationTaskId)}
                        disabled={actionBusy || !canCancelBranch}
                      >
                        {branchTaskActionId === branch.lastGenerationTaskId && canCancelBranch ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <X className="mr-2 h-4 w-4" />}
                        Cancel pending
                      </Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => handleViewBranchTaskLogs(branch.id, branch.lastGenerationTaskId)}
                      >
                        <History className="mr-2 h-4 w-4" />
                        Logs
                      </Button>
                    </div>
                  </Card>
                </div>
              );
            })}
            {management.branches.length === 0 && (
              <Card className="p-6 text-center text-muted-foreground animate-in fade-in-0 duration-300">
                {t("admin.repositories.management.noManageableBranches")}
              </Card>
            )}
          </div>

          <Card className="mt-4 overflow-hidden transition-all duration-300 hover:shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-3 border-b p-4">
              <div>
                <h3 className="font-semibold">Branch full generation tasks</h3>
                <p className="mt-1 text-xs text-muted-foreground">
                  Active {branchGenerationSummary.active} · Failed {branchGenerationSummary.failed} · Total {branchGenerationSummary.total}
                </p>
              </div>
              <div className="flex flex-wrap gap-2">
                <Badge variant="secondary">Completed {branchGenerationSummary.completed}</Badge>
                <Badge variant="outline">Cancelled {branchGenerationSummary.cancelled}</Badge>
              </div>
            </div>
            <div className="max-h-[420px] overflow-auto">
              <table className="w-full">
                <thead className="sticky top-0 bg-muted/80 backdrop-blur border-b">
                  <tr>
                    <th className="px-4 py-3 text-left text-xs font-medium">Task</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">Branch</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">Status</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">Created</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">Retry</th>
                    <th className="px-4 py-3 text-right text-xs font-medium">Actions</th>
                  </tr>
                </thead>
                <tbody className="divide-y">
                  {branchGenerationTasks.map((task, index) => {
                    const status = normalizeTaskStatus(task.status);
                    const canRetry = status === "failed" || status === "cancelled";
                    const canCancel = status === "pending";
                    const busy = branchTaskActionId === task.taskId;
                    return (
                      <tr
                        key={task.taskId}
                        className="animate-in fade-in-0 slide-in-from-bottom-1 transition-colors hover:bg-muted/40"
                        style={{ animationDelay: `${Math.min(index * 22, 220)}ms` }}
                      >
                        <td className="px-4 py-2 text-xs font-mono max-w-[220px] truncate">{task.taskId}</td>
                        <td className="px-4 py-2 text-xs">{task.branchName || task.branchId}</td>
                        <td className="px-4 py-2 text-xs">
                          <span className={`inline-flex rounded px-2 py-1 text-xs ${statusBadgeClass(task.status)}`}>
                            {getLocalizedTaskStatus(task.status)}
                          </span>
                          {task.errorMessage && (
                            <p className="mt-1 max-w-[300px] truncate text-[11px] text-red-500">{task.errorMessage}</p>
                          )}
                        </td>
                        <td className="px-4 py-2 text-xs whitespace-nowrap">{new Date(task.createdAt).toLocaleString(dateLocale)}</td>
                        <td className="px-4 py-2 text-xs">{task.retryCount}</td>
                        <td className="px-4 py-2">
                          <div className="flex justify-end gap-2">
                            <Button
                              variant="outline"
                              size="sm"
                              onClick={() => handleViewBranchTaskLogs(task.branchId, task.taskId)}
                            >
                              <History className="h-4 w-4" />
                            </Button>
                            <Button
                              variant="outline"
                              size="sm"
                              onClick={() => handleRetryBranchGeneration(task.taskId)}
                              disabled={busy || !canRetry}
                            >
                              {busy && canRetry ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
                            </Button>
                            <Button
                              variant="outline"
                              size="sm"
                              onClick={() => handleCancelBranchGeneration(task.taskId)}
                              disabled={busy || !canCancel}
                            >
                              {busy && canCancel ? <Loader2 className="h-4 w-4 animate-spin" /> : <X className="h-4 w-4" />}
                            </Button>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
              {branchGenerationTasks.length === 0 && (
                <div className="p-8 text-center text-sm text-muted-foreground">No branch full generation tasks</div>
              )}
            </div>
          </Card>
        </TabsContent>

        <TabsContent value="docs" className="mt-4 space-y-4 animate-in fade-in-0 slide-in-from-bottom-2 duration-300">
          <Card className="p-4 transition-all duration-300 hover:shadow-sm">
            <div className="grid gap-3 md:grid-cols-4">
              <div>
                <p className="mb-2 text-xs text-muted-foreground">{t("admin.repositories.management.filters.branch")}</p>
                <Select value={selectedBranchId} onValueChange={handleBranchChange}>
                  <SelectTrigger>
                    <SelectValue placeholder={t("admin.repositories.management.filters.selectBranch")} />
                  </SelectTrigger>
                  <SelectContent>
                    {management.branches.map((branch) => (
                      <SelectItem key={branch.id} value={branch.id}>
                        {branch.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div>
                <p className="mb-2 text-xs text-muted-foreground">{t("admin.repositories.management.filters.language")}</p>
                <Select value={selectedLanguage} onValueChange={handleLanguageChange} disabled={!selectedBranch}>
                  <SelectTrigger>
                    <SelectValue placeholder={t("admin.repositories.management.filters.selectLanguage")} />
                  </SelectTrigger>
                  <SelectContent>
                    {(selectedBranch?.languages ?? []).map((language) => (
                      <SelectItem key={language.id} value={language.languageCode}>
                        {language.languageCode}{language.isDefault ? ` ${t("admin.repositories.management.filters.defaultSuffix")}` : ""}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="md:col-span-2 flex items-end gap-2">
                <Button
                  variant="outline"
                  onClick={() => {
                    if (!confirmDiscardUnsavedChanges()) return;
                    loadTree();
                  }}
                  disabled={treeLoading}
                  className="transition-all duration-200"
                >
                  {treeLoading ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RefreshCw className="mr-2 h-4 w-4" />}
                  {t("admin.repositories.management.refreshDocTree")}
                </Button>
                <Button
                  onClick={handleRegenerateDocument}
                  disabled={!selectedDocSlug || regeneratingDoc || !selectedLanguageInfo}
                  className="transition-all duration-200 hover:-translate-y-0.5"
                >
                  {regeneratingDoc ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RotateCcw className="mr-2 h-4 w-4" />}
                  {t("admin.repositories.management.regenerateCurrentDoc")}
                </Button>
              </div>
            </div>
            <div className="mt-4 grid gap-3 md:grid-cols-3">
              <Card className="p-3">
                <p className="text-xs text-muted-foreground">{t("admin.repositories.management.loadedNodes")}</p>
                <p className="text-lg font-semibold">{docOptions.length}</p>
              </Card>
              <Card className="p-3">
                <p className="text-xs text-muted-foreground">{t("admin.repositories.management.currentLanguageDocCatalog")}</p>
                <p className="text-lg font-semibold">
                  {selectedLanguageInfo
                    ? `${selectedLanguageInfo.documentCount} / ${selectedLanguageInfo.catalogCount}`
                    : t("admin.repositories.management.notSelected")}
                </p>
              </Card>
              <Card className="p-3">
                <p className="mb-2 text-xs text-muted-foreground">{t("admin.repositories.management.currentLanguageCoverage")}</p>
                <div className="mb-1 flex items-center justify-between text-xs text-muted-foreground">
                  <span>{t("admin.repositories.management.coverage")}</span>
                  <span>{selectedLanguageCoverage}%</span>
                </div>
                <Progress value={selectedLanguageCoverage} className="h-2.5" />
              </Card>
            </div>
          </Card>

          <div className="grid gap-4 lg:grid-cols-[300px_1fr]">
            <Card className="p-3 transition-all duration-300 hover:shadow-sm">
              <div className="mb-3 flex items-center justify-between">
                <h3 className="text-sm font-semibold">{t("admin.repositories.management.docTree")}</h3>
                <Badge variant="outline">{t("admin.repositories.management.nodeCount", { count: docOptions.length })}</Badge>
              </div>
              <div className="max-h-[560px] overflow-auto space-y-1 pr-1">
                {docTreeNodes.length > 0 ? (
                  renderDocTreeNodes(docTreeNodes)
                ) : (
                  <p className="text-sm text-muted-foreground">{t("admin.repositories.management.noManageableDocs")}</p>
                )}
              </div>
            </Card>

            <Card className="p-4 transition-all duration-300 hover:shadow-sm">
              <div className="mb-3 flex items-center justify-between">
                <div>
                  <h3 className="font-semibold">{t("admin.repositories.management.docContent")}</h3>
                  <p className="text-xs text-muted-foreground">{selectedDocSlug || t("admin.repositories.management.noSelectedDoc")}</p>
                </div>
                <div className="flex items-center gap-2">
                  {isDocDirty && <Badge variant="destructive">{t("admin.repositories.management.unsaved")}</Badge>}
                  {selectedLanguageInfo && (
                    <Badge variant="secondary">
                      {selectedLanguageInfo.languageCode} · {t("admin.repositories.management.docCount", { count: selectedLanguageInfo.documentCount })}
                    </Badge>
                  )}
                </div>
              </div>
              {docLoading ? (
                <div className="flex h-[520px] items-center justify-center animate-in fade-in-0 duration-200">
                  <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
                </div>
              ) : doc?.exists ? (
                <div className="space-y-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="flex flex-wrap gap-2">
                      <Badge variant="outline">{t("admin.repositories.management.sourceFiles", { count: doc.sourceFiles.length })}</Badge>
                      {doc.sourceFiles.slice(0, 2).map((filePath) => (
                        <Badge key={filePath} variant="secondary" className="max-w-[280px] truncate">
                          {filePath}
                        </Badge>
                      ))}
                    </div>
                    <div className="flex items-center gap-2">
                      {!isEditingDoc ? (
                        <Button variant="outline" size="sm" onClick={handleStartEditDoc}>
                          <Pencil className="mr-2 h-4 w-4" />
                          {t("admin.repositories.management.editDoc")}
                        </Button>
                      ) : (
                        <>
                          <Button
                            size="sm"
                            onClick={handleSaveDocContent}
                            disabled={!isDocDirty || savingDoc}
                          >
                            {savingDoc ? (
                              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                            ) : (
                              <Save className="mr-2 h-4 w-4" />
                            )}
                            {t("admin.repositories.management.saveDoc")}
                          </Button>
                          <Button variant="outline" size="sm" onClick={handleCancelEditDoc} disabled={savingDoc}>
                            <X className="mr-2 h-4 w-4" />
                            {t("admin.repositories.management.cancelEdit")}
                          </Button>
                        </>
                      )}
                    </div>
                  </div>
                  {!isEditingDoc ? (
                    <pre
                      key={selectedDocSlug}
                      className="max-h-[520px] overflow-auto whitespace-pre-wrap rounded bg-muted p-4 text-xs leading-6 animate-in fade-in-0 duration-200"
                    >
                      {doc.content}
                    </pre>
                  ) : (
                    <div className="space-y-2">
                      <textarea
                        value={docDraft}
                        onChange={(event) => setDocDraft(event.target.value)}
                        className="h-[520px] w-full resize-none rounded-md border bg-background p-3 text-xs leading-6 outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      />
                      <div className="flex items-center justify-between text-xs text-muted-foreground">
                        <span>{t("admin.repositories.management.editHint")}</span>
                        <span>{t("admin.repositories.management.charCount", { count: docDraft.length })}</span>
                      </div>
                    </div>
                  )}
                </div>
              ) : (
                <div className="flex h-[520px] items-center justify-center text-sm text-muted-foreground animate-in fade-in-0 duration-200">
                  {t("admin.repositories.management.noDocContent")}
                </div>
              )}
            </Card>
          </div>
        </TabsContent>

        <TabsContent value="logs" className="mt-4 space-y-4 animate-in fade-in-0 slide-in-from-bottom-2 duration-300">
          <Card className="p-4 transition-all duration-300 hover:shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="space-y-1">
                <h3 className="font-semibold">{t("admin.repositories.management.logsTitle")}</h3>
                {logs && (
                  <p className="text-xs text-muted-foreground">
                    {t("admin.repositories.management.currentStepProgress", {
                      step: logs.currentStepName,
                      completed: logs.completedDocuments,
                      total: logs.totalDocuments,
                    })}
                  </p>
                )}
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <Select value={logBranchFilter} onValueChange={setLogBranchFilter}>
                  <SelectTrigger className="h-9 w-[180px]">
                    <SelectValue placeholder="Branch logs" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">All branches</SelectItem>
                    {management.branches.map((branch) => (
                      <SelectItem key={branch.id} value={branch.id}>
                        {branch.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Input
                  value={logTaskFilter}
                  onChange={(event) => setLogTaskFilter(event.target.value)}
                  placeholder="taskId"
                  className="h-9 w-[240px] font-mono text-xs"
                />
                <Button variant="outline" onClick={() => loadLogs()} disabled={logsLoading} className="transition-all duration-200">
                  {logsLoading ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <RefreshCw className="mr-2 h-4 w-4" />}
                  {t("admin.repositories.management.refreshLogs")}
                </Button>
              </div>
            </div>
            <div className="mt-3 rounded-lg border bg-muted/30 p-3">
              <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                <span className="inline-flex items-center gap-1">
                  <Clock3 className="h-3.5 w-3.5" />
                  {t("admin.repositories.management.logProgress")}
                </span>
                <span>{logProgress.completed} / {logProgress.total}</span>
              </div>
              <Progress value={logProgress.percent} className="h-2.5" />
            </div>
            <div className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
              {processingFlow.map((step) => (
                <div
                  key={step.index}
                  className={`rounded-md border px-3 py-2 text-xs transition-all ${
                    step.state === "done"
                      ? "border-emerald-500/30 bg-emerald-500/10 text-emerald-700"
                      : step.state === "active"
                        ? "border-blue-500/30 bg-blue-500/10 text-blue-700 ring-1 ring-blue-500/20"
                        : step.state === "failed"
                          ? "border-red-500/30 bg-red-500/10 text-red-700"
                          : "text-muted-foreground"
                  }`}
                >
                  <div className="flex items-center justify-between">
                    <span>{step.label}</span>
                    <span className="text-[10px]">
                      {step.state === "done"
                        ? t("admin.repositories.management.stepState.done")
                        : step.state === "active"
                          ? t("admin.repositories.management.stepState.active")
                          : step.state === "failed"
                            ? t("admin.repositories.management.stepState.failed")
                            : t("admin.repositories.management.stepState.pending")}
                    </span>
                  </div>
                </div>
              ))}
            </div>
          </Card>

          <Card className="p-0 overflow-hidden transition-all duration-300 hover:shadow-sm">
            <div className="max-h-[560px] overflow-auto">
              <table className="w-full">
                <thead className="sticky top-0 bg-muted/80 backdrop-blur border-b">
                  <tr>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.logColumns.time")}</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.logColumns.step")}</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.logColumns.type")}</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">Scope</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.logColumns.message")}</th>
                  </tr>
                </thead>
                <tbody className="divide-y">
                  {(logs?.logs ?? []).map((log, index) => (
                    <tr
                      key={log.id}
                      className="animate-in fade-in-0 slide-in-from-bottom-1 transition-colors hover:bg-muted/40"
                      style={{ animationDelay: `${Math.min(index * 18, 180)}ms` }}
                    >
                      <td className="px-4 py-2 text-xs text-muted-foreground whitespace-nowrap">
                        {new Date(log.createdAt).toLocaleString(dateLocale)}
                      </td>
                      <td className="px-4 py-2 text-xs">{log.stepName}</td>
                      <td className="px-4 py-2 text-xs">
                        {log.isAiOutput ? <Badge variant="secondary">AI</Badge> : <Badge variant="outline">{t("admin.repositories.management.logTypeSystem")}</Badge>}
                      </td>
                      <td className="px-4 py-2 text-xs">
                        <div className="space-y-1">
                          {log.branchId && <Badge variant="outline" className="font-mono">branch {log.branchId}</Badge>}
                          {log.generationTaskId && <Badge variant="secondary" className="font-mono">task {log.generationTaskId}</Badge>}
                          {!log.branchId && !log.generationTaskId && <span className="text-muted-foreground">repository</span>}
                        </div>
                      </td>
                      <td className="px-4 py-2 text-xs">{log.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {(logs?.logs.length ?? 0) === 0 && (
                <div className="p-8 text-center text-sm text-muted-foreground">{t("admin.repositories.management.noLogs")}</div>
              )}
            </div>
          </Card>
        </TabsContent>

        <TabsContent value="incremental" className="mt-4 space-y-4 animate-in fade-in-0 slide-in-from-bottom-2 duration-300">
          <Card className="p-4 transition-all duration-300 hover:shadow-sm">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="space-y-1">
                <h3 className="font-semibold">{t("admin.repositories.management.incrementalTitle")}</h3>
                <p className="text-xs text-muted-foreground">
                  {t("admin.repositories.management.incrementalSubtitle", {
                    branch: selectedBranch?.name ?? t("admin.repositories.management.notSelected"),
                  })}
                </p>
              </div>
              <Button
                onClick={handleTriggerIncremental}
                disabled={triggeringIncremental || !selectedBranch || !supportsGitOperations}
                title={supportsGitOperations ? t("admin.repositories.management.triggerCurrentBranchIncremental") : t("admin.repositories.management.incrementalNotSupported")}
                className="transition-all duration-200 hover:-translate-y-0.5"
              >
                {triggeringIncremental ? <Loader2 className="mr-2 h-4 w-4 animate-spin" /> : <Zap className="mr-2 h-4 w-4" />}
                {t("admin.repositories.management.triggerCurrentBranchIncremental")}
              </Button>
            </div>

            {!supportsGitOperations && (
              <p className="text-xs text-muted-foreground">
                {t("admin.repositories.management.incrementalNotSupported")}
              </p>
            )}

            <div className="mt-4 grid gap-3 md:grid-cols-3">
              <Card className="p-3">
                <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                  <span className="inline-flex items-center gap-1">
                    <CheckCircle2 className="h-3.5 w-3.5 text-green-500" />
                    {t("admin.repositories.management.successRate")}
                  </span>
                  <span>{incrementalSummary.successRate}%</span>
                </div>
                <Progress value={incrementalSummary.successRate} className="h-2.5" />
              </Card>
              <Card className="p-3">
                <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                  <span className="inline-flex items-center gap-1">
                    <Clock3 className="h-3.5 w-3.5 text-blue-500" />
                    {t("admin.repositories.management.activeRate")}
                  </span>
                  <span>{incrementalSummary.activeRate}%</span>
                </div>
                <Progress value={incrementalSummary.activeRate} className="h-2.5" />
              </Card>
              <Card className="p-3">
                <p className="text-xs text-muted-foreground">{t("admin.repositories.management.failedAlerts")}</p>
                <p className="mt-1 inline-flex items-center gap-1 text-lg font-semibold">
                  <AlertTriangle className="h-4 w-4 text-amber-500" />
                  {incrementalSummary.failed}
                </p>
              </Card>
            </div>

            <div className="mt-3 flex flex-wrap gap-2">
              <Badge variant="outline">{t("admin.repositories.management.totalTasks", { count: incrementalSummary.total })}</Badge>
              <Badge variant="secondary">{t("admin.repositories.management.processingTasks", { count: incrementalSummary.processing })}</Badge>
              <Badge variant="secondary">{t("admin.repositories.management.pendingTasks", { count: incrementalSummary.pending })}</Badge>
              <Badge variant="secondary">{t("admin.repositories.management.completedTasks", { count: incrementalSummary.completed })}</Badge>
              <Badge variant="destructive">{t("admin.repositories.management.failedTasks", { count: incrementalSummary.failed })}</Badge>
            </div>
            <div className="mt-3 rounded-lg border bg-muted/30 p-3">
              <div className="mb-2 flex items-center justify-between text-xs text-muted-foreground">
                <span>{t("admin.repositories.management.taskStatusDistribution")}</span>
                <span>{t("admin.repositories.management.taskRecordCount", { count: incrementalSummary.total })}</span>
              </div>
              <div className="h-2.5 w-full overflow-hidden rounded-full bg-muted">
                <div className="flex h-full w-full">
                  {incrementalStatusSegments.map((segment) => (
                    <div
                      key={segment.key}
                      className={`h-full transition-all duration-500 ${segment.color}`}
                      style={{ width: `${segment.percent}%` }}
                    />
                  ))}
                </div>
              </div>
              <div className="mt-2 flex flex-wrap gap-2">
                {incrementalStatusSegments.map((segment) => (
                  <Badge key={segment.key} variant="secondary" className="gap-1">
                    <span className={`inline-block h-2 w-2 rounded-full ${segment.color}`} />
                    {segment.label} {segment.count}
                  </Badge>
                ))}
              </div>
            </div>
          </Card>

          <Card className="p-0 overflow-hidden transition-all duration-300 hover:shadow-sm">
            <div className="max-h-[560px] overflow-auto">
              <table className="w-full">
                <thead className="sticky top-0 bg-muted/80 backdrop-blur border-b">
                  <tr>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.taskColumns.taskId")}</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.taskColumns.branch")}</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.taskColumns.status")}</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.taskColumns.createdAt")}</th>
                    <th className="px-4 py-3 text-left text-xs font-medium">{t("admin.repositories.management.taskColumns.retry")}</th>
                    <th className="px-4 py-3 text-right text-xs font-medium">{t("admin.repositories.management.taskColumns.actions")}</th>
                  </tr>
                </thead>
                <tbody className="divide-y">
                  {management.recentIncrementalTasks.map((task, index) => (
                    <tr
                      key={task.taskId}
                      className="animate-in fade-in-0 slide-in-from-bottom-1 transition-colors hover:bg-muted/40"
                      style={{ animationDelay: `${Math.min(index * 22, 220)}ms` }}
                    >
                      <td className="px-4 py-2 text-xs font-mono max-w-[220px] truncate">{task.taskId}</td>
                      <td className="px-4 py-2 text-xs">{task.branchName || task.branchId}</td>
                      <td className="px-4 py-2 text-xs">
                        <span className={`inline-flex rounded px-2 py-1 text-xs ${statusBadgeClass(task.status)}`}>
                          {getLocalizedTaskStatus(task.status)}
                        </span>
                        {task.errorMessage && (
                          <p className="mt-1 max-w-[260px] truncate text-[11px] text-red-500">{task.errorMessage}</p>
                        )}
                      </td>
                      <td className="px-4 py-2 text-xs whitespace-nowrap">{new Date(task.createdAt).toLocaleString(dateLocale)}</td>
                      <td className="px-4 py-2 text-xs">{task.retryCount}</td>
                      <td className="px-4 py-2">
                        <div className="flex justify-end gap-2">
                          <Button
                            variant="outline"
                            size="sm"
                            onClick={() => handleRefreshTask(task.taskId)}
                            disabled={taskRefreshingId === task.taskId}
                          >
                            {taskRefreshingId === task.taskId ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                          </Button>
                          <Button
                            variant="outline"
                            size="sm"
                            onClick={() => handleRetryTask(task.taskId)}
                            disabled={taskRetryingId === task.taskId || normalizeTaskStatus(task.status) !== "failed"}
                            title={
                              normalizeTaskStatus(task.status) === "failed"
                                ? t("admin.repositories.management.retryFailedTask")
                                : t("admin.repositories.management.retryOnlyFailed")
                            }
                          >
                            {taskRetryingId === task.taskId ? <Loader2 className="h-4 w-4 animate-spin" /> : <RotateCcw className="h-4 w-4" />}
                          </Button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {management.recentIncrementalTasks.length === 0 && (
                <div className="p-8 text-center text-sm text-muted-foreground animate-in fade-in-0 duration-200">
                  {t("admin.repositories.management.noIncrementalTasks")}
                </div>
              )}
            </div>
          </Card>
        </TabsContent>
      </Tabs>
    </div>
  );
}
