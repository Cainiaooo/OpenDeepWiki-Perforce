"use client";

import React, { useEffect, useState, useCallback } from "react";
import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  getDepartmentTree,
  createDepartment,
  updateDepartment,
  deleteDepartment,
  getDepartmentUsers,
  addUserToDepartment,
  removeUserFromDepartment,
  getDepartmentRepositories,
  assignRepositoryToDepartment,
  removeRepositoryFromDepartment,
  getUsers,
  getRepositories,
  AdminDepartment,
  DepartmentUser,
  DepartmentRepository,
} from "@/lib/admin-api";
import {
  Loader2,
  Trash2,
  Edit,
  RefreshCw,
  Plus,
  Building2,
  FolderTree,
  Users,
  GitBranch,
  UserPlus,
  X,
  ChevronRight,
  ChevronDown,
  Search,
} from "lucide-react";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";


interface FormData {
  name: string;
  parentId: string;
  description: string;
  sortOrder: number;
  isActive: boolean;
}

const defaultFormData: FormData = {
  name: "",
  parentId: "",
  description: "",
  sortOrder: 0,
  isActive: true,
};

// 树形节点组件
function DepartmentTreeNode({
  dept,
  level,
  selectedId,
  expandedIds,
  onSelect,
  onToggle,
  onEdit,
  onDelete,
  t,
}: {
  dept: AdminDepartment;
  level: number;
  selectedId: string | null;
  expandedIds: Set<string>;
  onSelect: (dept: AdminDepartment) => void;
  onToggle: (id: string) => void;
  onEdit: (dept: AdminDepartment) => void;
  onDelete: (id: string) => void;
  t: (key: string) => string;
}) {
  const hasChildren = dept.children && dept.children.length > 0;
  const isExpanded = expandedIds.has(dept.id);
  const isSelected = selectedId === dept.id;

  return (
    <div>
      <div
        className={`flex items-center gap-2 p-2 rounded-md cursor-pointer transition-colors hover:bg-muted ${
          isSelected ? "bg-primary/10 border border-primary/30" : ""
        }`}
        style={{ paddingLeft: `${level * 16 + 8}px` }}
        onClick={() => onSelect(dept)}
      >
        {hasChildren ? (
          <button
            onClick={(e) => { e.stopPropagation(); onToggle(dept.id); }}
            className="p-0.5 hover:bg-muted-foreground/20 rounded"
          >
            {isExpanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
          </button>
        ) : (
          <span className="w-5" />
        )}
        <Building2 className="h-4 w-4 text-primary flex-shrink-0" />
        <span className="flex-1 font-medium text-sm truncate">{dept.name}</span>
        <span className={`text-xs px-2 py-0.5 rounded-full flex-shrink-0 ${
          dept.isActive
            ? "bg-green-500/20 text-green-400"
            : "bg-muted text-muted-foreground"
        }`}>
          {dept.isActive ? t('admin.departments.enabled') : t('admin.departments.disabled')}
        </span>
        <div className="flex gap-0.5 flex-shrink-0">
          <Button variant="ghost" size="icon" className="h-7 w-7 hover:bg-muted-foreground/20" onClick={(e) => { e.stopPropagation(); onEdit(dept); }}>
            <Edit className="h-3.5 w-3.5" />
          </Button>
          <Button variant="ghost" size="icon" className="h-7 w-7 hover:bg-red-500/20" onClick={(e) => { e.stopPropagation(); onDelete(dept.id); }}>
            <Trash2 className="h-3.5 w-3.5 text-red-500" />
          </Button>
        </div>
      </div>
      {hasChildren && isExpanded && (
        <div>
          {dept.children!.map((child) => (
            <DepartmentTreeNode
              key={child.id}
              dept={child}
              level={level + 1}
              selectedId={selectedId}
              expandedIds={expandedIds}
              onSelect={onSelect}
              onToggle={onToggle}
              onEdit={onEdit}
              onDelete={onDelete}
              t={t}
            />
          ))}
        </div>
      )}
    </div>
  );
}


// 扁平化部门树用于下拉选择
function flattenDepartments(depts: AdminDepartment[], level = 0): { id: string; name: string; level: number }[] {
  const result: { id: string; name: string; level: number }[] = [];
  for (const dept of depts) {
    result.push({ id: dept.id, name: dept.name, level });
    if (dept.children && dept.children.length > 0) {
      result.push(...flattenDepartments(dept.children, level + 1));
    }
  }
  return result;
}

export default function AdminDepartmentsPage() {
  const [departmentTree, setDepartmentTree] = useState<AdminDepartment[]>([]);
  const [loading, setLoading] = useState(true);
  const [showDialog, setShowDialog] = useState(false);
  const [editingDept, setEditingDept] = useState<AdminDepartment | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [formData, setFormData] = useState<FormData>(defaultFormData);
  const t = useTranslations();

  // 树形展开状态
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());

  // 详情面板状态
  const [selectedDept, setSelectedDept] = useState<AdminDepartment | null>(null);
  const [deptUsers, setDeptUsers] = useState<DepartmentUser[]>([]);
  const [deptRepos, setDeptRepos] = useState<DepartmentRepository[]>([]);
  const [detailLoading, setDetailLoading] = useState(false);

  // 添加用户对话框状态
  const [showAddUserDialog, setShowAddUserDialog] = useState(false);
  const [userSearchKeyword, setUserSearchKeyword] = useState("");
  const [userSearchResults, setUserSearchResults] = useState<{ id: string; name: string; email?: string }[]>([]);
  const [userPage, setUserPage] = useState(1);
  const [userTotal, setUserTotal] = useState(0);
  const [userLoading, setUserLoading] = useState(false);
  const [selectedUserId, setSelectedUserId] = useState("");
  const [isManager, setIsManager] = useState(false);

  // 添加仓库对话框状态
  const [showAddRepoDialog, setShowAddRepoDialog] = useState(false);
  const [repoSearchKeyword, setRepoSearchKeyword] = useState("");
  const [repoSearchResults, setRepoSearchResults] = useState<{ id: string; name: string; orgName: string }[]>([]);
  const [repoPage, setRepoPage] = useState(1);
  const [repoTotal, setRepoTotal] = useState(0);
  const [repoLoading, setRepoLoading] = useState(false);
  const [selectedRepoId, setSelectedRepoId] = useState("");

  const pageSize = 10;

  const fetchData = useCallback(async () => {
    setLoading(true);
    try {
      const result = await getDepartmentTree();
      setDepartmentTree(result);
      // 默认展开所有
      const allIds = new Set<string>();
      const collectIds = (depts: AdminDepartment[]) => {
        for (const d of depts) {
          allIds.add(d.id);
          if (d.children) collectIds(d.children);
        }
      };
      collectIds(result);
      setExpandedIds(allIds);
    } catch (error) {
      console.error("Failed to fetch departments:", error);
      toast.error(t('admin.toast.fetchDeptFailed'));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  const fetchDeptDetail = async (dept: AdminDepartment) => {
    setSelectedDept(dept);
    setDetailLoading(true);
    try {
      const [users, repos] = await Promise.all([
        getDepartmentUsers(dept.id),
        getDepartmentRepositories(dept.id),
      ]);
      setDeptUsers(users);
      setDeptRepos(repos);
    } catch (error) {
      toast.error(t('admin.toast.fetchDeptDetailFailed'));
    } finally {
      setDetailLoading(false);
    }
  };

  const toggleExpand = (id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const openCreateDialog = () => {
    setEditingDept(null);
    setFormData(defaultFormData);
    setShowDialog(true);
  };

  const openEditDialog = (dept: AdminDepartment) => {
    setEditingDept(dept);
    setFormData({
      name: dept.name,
      parentId: dept.parentId || "",
      description: dept.description || "",
      sortOrder: dept.sortOrder,
      isActive: dept.isActive,
    });
    setShowDialog(true);
  };

  const handleSave = async () => {
    if (!formData.name.trim()) {
      toast.error(t('admin.toast.enterDeptName'));
      return;
    }
    try {
      const payload = {
        name: formData.name,
        parentId: formData.parentId || undefined,
        description: formData.description || undefined,
        sortOrder: formData.sortOrder,
        isActive: formData.isActive,
      };
      if (editingDept) {
        await updateDepartment(editingDept.id, payload);
        toast.success(t('admin.toast.updateSuccess'));
      } else {
        await createDepartment(payload);
        toast.success(t('admin.toast.createSuccess'));
      }
      setShowDialog(false);
      fetchData();
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : "";
      toast.error(message || t('admin.toast.operationFailed'));
    }
  };

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await deleteDepartment(deleteId);
      toast.success(t('admin.toast.deleteSuccess'));
      setDeleteId(null);
      if (selectedDept?.id === deleteId) {
        setSelectedDept(null);
      }
      fetchData();
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : "";
      toast.error(message || t('admin.toast.deleteFailed'));
    }
  };


  // 搜索用户
  const searchUsers = useCallback(async (keyword: string, page: number) => {
    setUserLoading(true);
    try {
      const result = await getUsers(page, pageSize, keyword);
      setUserSearchResults(result.items.map(u => ({ id: u.id, name: u.name, email: u.email })));
      setUserTotal(result.total);
    } catch (error) {
      toast.error(t('admin.toast.searchUserFailed'));
    } finally {
      setUserLoading(false);
    }
  }, []);

  const openAddUserDialog = () => {
    setUserSearchKeyword("");
    setUserPage(1);
    setSelectedUserId("");
    setIsManager(false);
    setShowAddUserDialog(true);
    searchUsers("", 1);
  };

  const handleUserSearch = () => {
    setUserPage(1);
    searchUsers(userSearchKeyword, 1);
  };

  const handleUserPageChange = (newPage: number) => {
    setUserPage(newPage);
    searchUsers(userSearchKeyword, newPage);
  };

  const handleAddUser = async () => {
    if (!selectedDept || !selectedUserId) return;
    try {
      await addUserToDepartment(selectedDept.id, selectedUserId, isManager);
      toast.success(t('admin.toast.addSuccess'));
      setShowAddUserDialog(false);
      fetchDeptDetail(selectedDept);
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : "";
      toast.error(message || t('admin.toast.addFailed'));
    }
  };

  const handleRemoveUser = async (userId: string) => {
    if (!selectedDept) return;
    try {
      await removeUserFromDepartment(selectedDept.id, userId);
      toast.success(t('admin.toast.removeSuccess'));
      fetchDeptDetail(selectedDept);
    } catch (error) {
      toast.error(t('admin.toast.removeFailed'));
    }
  };

  // 搜索仓库
  const searchRepos = useCallback(async (keyword: string, page: number) => {
    setRepoLoading(true);
    try {
      const result = await getRepositories(page, pageSize, keyword);
      setRepoSearchResults(result.items.map(r => ({ id: r.id, name: r.repoName, orgName: r.orgName })));
      setRepoTotal(result.total);
    } catch (error) {
      toast.error(t('admin.toast.searchRepoFailed'));
    } finally {
      setRepoLoading(false);
    }
  }, []);

  const openAddRepoDialog = () => {
    setRepoSearchKeyword("");
    setRepoPage(1);
    setSelectedRepoId("");
    setShowAddRepoDialog(true);
    searchRepos("", 1);
  };

  const handleRepoSearch = () => {
    setRepoPage(1);
    searchRepos(repoSearchKeyword, 1);
  };

  const handleRepoPageChange = (newPage: number) => {
    setRepoPage(newPage);
    searchRepos(repoSearchKeyword, newPage);
  };

  const handleAddRepo = async () => {
    if (!selectedDept || !selectedRepoId) return;
    try {
      const assigneeId = deptUsers[0]?.userId || "";
      await assignRepositoryToDepartment(selectedDept.id, selectedRepoId, assigneeId);
      toast.success(t('admin.toast.assignSuccess'));
      setShowAddRepoDialog(false);
      fetchDeptDetail(selectedDept);
    } catch (error: unknown) {
      const message = error instanceof Error ? error.message : "";
      toast.error(message || t('admin.toast.assignFailed'));
    }
  };

  const handleRemoveRepo = async (repositoryId: string) => {
    if (!selectedDept) return;
    try {
      await removeRepositoryFromDepartment(selectedDept.id, repositoryId);
      toast.success(t('admin.toast.removeSuccess'));
      fetchDeptDetail(selectedDept);
    } catch (error) {
      toast.error(t('admin.toast.removeFailed'));
    }
  };

  const flatDepts = flattenDepartments(departmentTree);
  const userTotalPages = Math.ceil(userTotal / pageSize);
  const repoTotalPages = Math.ceil(repoTotal / pageSize);


  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">{t('admin.departments.title')}</h1>
        <div className="flex gap-2">
          <Button variant="outline" onClick={fetchData}>
            <RefreshCw className="mr-2 h-4 w-4" />
            {t('admin.common.refresh')}
          </Button>
          <Button onClick={openCreateDialog}>
            <Plus className="mr-2 h-4 w-4" />
            {t('admin.departments.createDept')}
          </Button>
        </div>
      </div>

      <div className="grid gap-6 lg:grid-cols-3">
        {/* 部门树 */}
        <div className="lg:col-span-1">
          <Card className="p-4">
            <h3 className="font-semibold mb-3 flex items-center gap-2">
              <FolderTree className="h-4 w-4" />
              {t('admin.departments.structure')}
            </h3>
            {loading ? (
              <div className="flex h-48 items-center justify-center">
                <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
              </div>
            ) : departmentTree.length === 0 ? (
              <div className="flex h-48 flex-col items-center justify-center text-muted-foreground">
                <Building2 className="h-10 w-10 mb-2 opacity-50" />
                <p>{t('admin.departments.noDepartments')}</p>
              </div>
            ) : (
              <div className="space-y-1 max-h-[500px] overflow-y-auto">
                {departmentTree.map((dept) => (
                  <DepartmentTreeNode
                    key={dept.id}
                    dept={dept}
                    level={0}
                    selectedId={selectedDept?.id || null}
                    expandedIds={expandedIds}
                    onSelect={fetchDeptDetail}
                    onToggle={toggleExpand}
                    onEdit={openEditDialog}
                    onDelete={setDeleteId}
                    t={t}
                  />
                ))}
              </div>
            )}
          </Card>
        </div>

        {/* 部门详情 */}
        <div className="lg:col-span-2">
          {!selectedDept ? (
            <Card className="flex h-64 flex-col items-center justify-center">
              <FolderTree className="h-12 w-12 text-muted-foreground/50" />
              <p className="mt-4 text-muted-foreground">{t('admin.departments.selectToView')}</p>
            </Card>
          ) : detailLoading ? (
            <Card className="flex h-64 items-center justify-center">
              <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
            </Card>
          ) : (
            <Card className="p-6">
              <div className="mb-4">
                <h2 className="text-xl font-semibold">{selectedDept.name}</h2>
                {selectedDept.description && (
                  <p className="text-sm text-muted-foreground mt-1">{selectedDept.description}</p>
                )}
              </div>

              <Tabs defaultValue="users">
                <TabsList>
                  <TabsTrigger value="users">
                    <Users className="mr-2 h-4 w-4" />
                    {t('admin.departments.users')} ({deptUsers.length})
                  </TabsTrigger>
                  <TabsTrigger value="repos">
                    <GitBranch className="mr-2 h-4 w-4" />
                    {t('admin.departments.repositories')} ({deptRepos.length})
                  </TabsTrigger>
                </TabsList>

                <TabsContent value="users" className="mt-4">
                  <div className="flex justify-end mb-4">
                    <Button size="sm" onClick={openAddUserDialog}>
                      <UserPlus className="mr-2 h-4 w-4" />
                      {t('admin.departments.addUser')}
                    </Button>
                  </div>
                  {deptUsers.length === 0 ? (
                    <p className="text-center text-muted-foreground py-8">{t('admin.departments.noUsers')}</p>
                  ) : (
                    <div className="space-y-2">
                      {deptUsers.map((user) => (
                        <div key={user.id} className="flex items-center justify-between p-3 bg-muted/30 rounded-lg border border-border/50">
                          <div className="flex items-center gap-3">
                            <div className="h-8 w-8 rounded-full bg-primary/20 flex items-center justify-center">
                              <Users className="h-4 w-4 text-primary" />
                            </div>
                            <div>
                              <p className="font-medium text-sm">{user.userName}</p>
                              <p className="text-xs text-muted-foreground">{user.email}</p>
                            </div>
                            {user.isManager && (
                              <span className="text-xs bg-primary/20 text-primary px-2 py-0.5 rounded-full">{t('admin.departments.manager')}</span>
                            )}
                          </div>
                          <Button variant="ghost" size="icon" className="hover:bg-red-500/20" onClick={() => handleRemoveUser(user.userId)}>
                            <X className="h-4 w-4 text-red-500" />
                          </Button>
                        </div>
                      ))}
                    </div>
                  )}
                </TabsContent>

                <TabsContent value="repos" className="mt-4">
                  <div className="flex justify-end mb-4">
                    <Button size="sm" onClick={openAddRepoDialog}>
                      <Plus className="mr-2 h-4 w-4" />
                      {t('admin.departments.assignRepo')}
                    </Button>
                  </div>
                  {deptRepos.length === 0 ? (
                    <p className="text-center text-muted-foreground py-8">{t('admin.departments.noRepos')}</p>
                  ) : (
                    <div className="space-y-2">
                      {deptRepos.map((repo) => (
                        <div key={repo.id} className="flex items-center justify-between p-3 bg-muted/30 rounded-lg border border-border/50">
                          <div className="flex items-center gap-3">
                            <div className="h-8 w-8 rounded-full bg-primary/20 flex items-center justify-center">
                              <GitBranch className="h-4 w-4 text-primary" />
                            </div>
                            <div>
                              <p className="font-medium text-sm">{repo.orgName}/{repo.repoName}</p>
                              {repo.assigneeUserName && (
                                <p className="text-xs text-muted-foreground">{t('admin.departments.assignee')}: {repo.assigneeUserName}</p>
                              )}
                            </div>
                          </div>
                          <Button variant="ghost" size="icon" className="hover:bg-red-500/20" onClick={() => handleRemoveRepo(repo.repositoryId)}>
                            <X className="h-4 w-4 text-red-500" />
                          </Button>
                        </div>
                      ))}
                    </div>
                  )}
                </TabsContent>
              </Tabs>
            </Card>
          )}
        </div>
      </div>


      {/* 新增/编辑部门对话框 */}
      <Dialog open={showDialog} onOpenChange={setShowDialog}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{editingDept ? t('admin.departments.editDept') : t('admin.departments.createDept')}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div>
              <label className="text-sm font-medium">{t('admin.departments.deptName')} *</label>
              <Input
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder={t('admin.departments.enterDeptName')}
              />
            </div>
            <div>
              <label className="text-sm font-medium">{t('admin.departments.parentDept')}</label>
              <Select
                value={formData.parentId || "__none__"}
                onValueChange={(value) => setFormData({ ...formData, parentId: value === "__none__" ? "" : value })}
              >
                <SelectTrigger>
                  <SelectValue placeholder={t('admin.departments.selectParentDept')} />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="__none__">{t('admin.departments.none')}</SelectItem>
                  {flatDepts
                    .filter((d) => d.id !== editingDept?.id)
                    .map((dept) => (
                      <SelectItem key={dept.id} value={dept.id}>
                        {"　".repeat(dept.level)}{dept.name}
                      </SelectItem>
                    ))}
                </SelectContent>
              </Select>
            </div>
            <div>
              <label className="text-sm font-medium">{t('admin.departments.description')}</label>
              <Textarea
                value={formData.description}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                placeholder={t('admin.departments.enterDeptDesc')}
                rows={3}
              />
            </div>
            <div>
              <label className="text-sm font-medium">{t('admin.departments.sortOrder')}</label>
              <Input
                type="number"
                value={formData.sortOrder}
                onChange={(e) => setFormData({ ...formData, sortOrder: parseInt(e.target.value) || 0 })}
              />
            </div>
            <div className="flex items-center justify-between">
              <label className="text-sm font-medium">{t('admin.departments.activeStatus')}</label>
              <Switch
                checked={formData.isActive}
                onCheckedChange={(checked) => setFormData({ ...formData, isActive: checked })}
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowDialog(false)}>{t('admin.common.cancel')}</Button>
            <Button onClick={handleSave}>{editingDept ? t('admin.common.save') : t('admin.common.create')}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* 添加用户对话框 - 支持搜索和分页 */}
      <Dialog open={showAddUserDialog} onOpenChange={setShowAddUserDialog}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>{t('admin.departments.addUserToDept')}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div className="flex gap-2">
              <Input
                placeholder={t('admin.departments.searchUserPlaceholder')}
                value={userSearchKeyword}
                onChange={(e) => setUserSearchKeyword(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && handleUserSearch()}
              />
              <Button variant="outline" onClick={handleUserSearch}>
                <Search className="h-4 w-4" />
              </Button>
            </div>

            <div className="border border-border rounded-lg max-h-[300px] overflow-y-auto">
              {userLoading ? (
                <div className="flex items-center justify-center py-8">
                  <Loader2 className="h-6 w-6 animate-spin" />
                </div>
              ) : userSearchResults.length === 0 ? (
                <p className="text-center text-muted-foreground py-8">{t('admin.departments.noSearchResults')}</p>
              ) : (
                <div className="divide-y divide-border">
                  {userSearchResults
                    .filter(u => !deptUsers.some(du => du.userId === u.id))
                    .map((user) => (
                      <div
                        key={user.id}
                        className={`flex items-center gap-3 p-3 cursor-pointer transition-colors hover:bg-muted ${
                          selectedUserId === user.id ? "bg-primary/10" : ""
                        }`}
                        onClick={() => setSelectedUserId(user.id)}
                      >
                        <div className="h-8 w-8 rounded-full bg-primary/20 flex items-center justify-center">
                          <Users className="h-4 w-4 text-primary" />
                        </div>
                        <div className="flex-1">
                          <p className="font-medium text-sm">{user.name}</p>
                          <p className="text-xs text-muted-foreground">{user.email}</p>
                        </div>
                        {selectedUserId === user.id && (
                          <div className="h-4 w-4 rounded-full bg-primary" />
                        )}
                      </div>
                    ))}
                </div>
              )}
            </div>

            {userTotalPages > 1 && (
              <div className="flex items-center justify-center gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={userPage <= 1}
                  onClick={() => handleUserPageChange(userPage - 1)}
                >
                  {t('admin.departments.prevPage')}
                </Button>
                <span className="text-sm text-muted-foreground">
                  {userPage} / {userTotalPages}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={userPage >= userTotalPages}
                  onClick={() => handleUserPageChange(userPage + 1)}
                >
                  {t('admin.departments.nextPage')}
                </Button>
              </div>
            )}

            <div className="flex items-center justify-between">
              <label className="text-sm font-medium">{t('admin.departments.setAsManager')}</label>
              <Switch checked={isManager} onCheckedChange={setIsManager} />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowAddUserDialog(false)}>{t('admin.common.cancel')}</Button>
            <Button onClick={handleAddUser} disabled={!selectedUserId}>{t('admin.common.add')}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>


      {/* 分配仓库对话框 - 支持搜索和分页 */}
      <Dialog open={showAddRepoDialog} onOpenChange={setShowAddRepoDialog}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>{t('admin.departments.assignRepoToDept')}</DialogTitle>
          </DialogHeader>
          <div className="space-y-4">
            <div className="flex gap-2">
              <Input
                placeholder={t('admin.departments.searchRepoPlaceholder')}
                value={repoSearchKeyword}
                onChange={(e) => setRepoSearchKeyword(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && handleRepoSearch()}
              />
              <Button variant="outline" onClick={handleRepoSearch}>
                <Search className="h-4 w-4" />
              </Button>
            </div>

            <div className="border border-border rounded-lg max-h-[300px] overflow-y-auto">
              {repoLoading ? (
                <div className="flex items-center justify-center py-8">
                  <Loader2 className="h-6 w-6 animate-spin" />
                </div>
              ) : repoSearchResults.length === 0 ? (
                <p className="text-center text-muted-foreground py-8">{t('admin.departments.noSearchResults')}</p>
              ) : (
                <div className="divide-y divide-border">
                  {repoSearchResults
                    .filter(r => !deptRepos.some(dr => dr.repositoryId === r.id))
                    .map((repo) => (
                      <div
                        key={repo.id}
                        className={`flex items-center gap-3 p-3 cursor-pointer transition-colors hover:bg-muted ${
                          selectedRepoId === repo.id ? "bg-primary/10" : ""
                        }`}
                        onClick={() => setSelectedRepoId(repo.id)}
                      >
                        <div className="h-8 w-8 rounded-full bg-primary/20 flex items-center justify-center">
                          <GitBranch className="h-4 w-4 text-primary" />
                        </div>
                        <div className="flex-1">
                          <p className="font-medium text-sm">{repo.orgName}/{repo.name}</p>
                        </div>
                        {selectedRepoId === repo.id && (
                          <div className="h-4 w-4 rounded-full bg-primary" />
                        )}
                      </div>
                    ))}
                </div>
              )}
            </div>

            {repoTotalPages > 1 && (
              <div className="flex items-center justify-center gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={repoPage <= 1}
                  onClick={() => handleRepoPageChange(repoPage - 1)}
                >
                  {t('admin.departments.prevPage')}
                </Button>
                <span className="text-sm text-muted-foreground">
                  {repoPage} / {repoTotalPages}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={repoPage >= repoTotalPages}
                  onClick={() => handleRepoPageChange(repoPage + 1)}
                >
                  {t('admin.departments.nextPage')}
                </Button>
              </div>
            )}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setShowAddRepoDialog(false)}>{t('admin.common.cancel')}</Button>
            <Button onClick={handleAddRepo} disabled={!selectedRepoId}>{t('admin.common.assign')}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* 删除确认对话框 */}
      <AlertDialog open={!!deleteId} onOpenChange={() => setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t('admin.common.confirmDelete')}</AlertDialogTitle>
            <AlertDialogDescription>
              {t('admin.departments.deleteWarning')}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t('admin.common.cancel')}</AlertDialogCancel>
            <AlertDialogAction onClick={handleDelete} className="bg-red-600 hover:bg-red-700">{t('admin.common.delete')}</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
