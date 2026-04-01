# Requirements: v16.0 FunLang AST 동기화

**Defined:** 2026-04-01
**Core Value:** FunLang AST 변경에 맞춰 Compiler를 동기화하여 빌드 복구 및 중첩 모듈 지원

## Milestone Requirements

### Namespace 제거 (빌드 수정)

- [x] **NS-01**: Elaboration.fs prePassDecls에서 NamespaceDecl 패턴 제거 (line 4237-4238)
- [x] **NS-02**: Elaboration.fs flattenDecls에서 NamespaceDecl 패턴 제거 (line 4283)
- [x] **NS-03**: Elaboration.fs elaborateTypeclasses에서 NamespaceDecl 패턴 제거 (line 4380-4381)
- [x] **NS-04**: Elaboration.fs elaborateProgram에서 NamespacedModule 패턴 제거 (line 4442)
- [x] **NS-05**: 관련 주석에서 "NamespaceDecl" 참조 제거/수정

### 중첩 모듈 (Nested Module)

- [ ] **NEST-01**: flattenDecls에서 중첩 모듈 prefix를 전체 경로로 변경 (Inner_foo → Outer_Inner_foo)
- [ ] **NEST-02**: collectModuleMembers에서 중첩 모듈을 전체 경로 키로 등록 (e.g., "Outer.Inner")
- [ ] **NEST-03**: FieldAccess에서 중첩 qualified access 지원 (Outer.Inner.foo → Outer_Inner_foo)
- [ ] **NEST-04**: open multi-segment에서 전체 경로 키로 lookup (open Outer.Inner → "Outer.Inner" 키 사용)

### 테스트

- [x] **TEST-01**: 빌드 성공 + 기존 232 E2E 테스트 통과
- [ ] **TEST-02**: 중첩 모듈 E2E 테스트 — Outer.Inner.value qualified access + open Outer.Inner

## Future Requirements

None.

## Out of Scope

- Module export filtering (FunLang 인터프리터 전용 — 컴파일러의 flat name-prefixing에는 불필요)
- Error code system (E03xx~E07xx) — 컴파일러에 타입 시스템 없음
- funproj.toml 빌드 시스템 — 인터프리터 전용

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| NS-01 | Phase 58 | Complete |
| NS-02 | Phase 58 | Complete |
| NS-03 | Phase 58 | Complete |
| NS-04 | Phase 58 | Complete |
| NS-05 | Phase 58 | Complete |
| NEST-01 | Phase 59 | Pending |
| NEST-02 | Phase 59 | Pending |
| NEST-03 | Phase 59 | Pending |
| NEST-04 | Phase 59 | Pending |
| TEST-01 | Phase 58 | Complete |
| TEST-02 | Phase 59 | Pending |

**Coverage:**
- v16.0 requirements: 11 total
- Mapped to phases: 11
- Unmapped: 0

---
*Created: 2026-04-01*
