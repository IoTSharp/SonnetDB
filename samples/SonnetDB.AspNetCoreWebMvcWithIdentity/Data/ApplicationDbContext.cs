using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SonnetDB.AspNetCoreWebMvcWithIdentity.Data
{
    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
    {
        /// <summary>
        /// 配置 Identity 模型，并保留既有迁移的锁定截止时间列类型。
        /// </summary>
        /// <param name="builder">模型生成器。</param>
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // 既有迁移使用毫秒精度的 DATETIME；原生 DATETIMEOFFSET 须通过显式迁移启用。
            builder.Entity<IdentityUser>()
                .Property(user => user.LockoutEnd)
                .HasColumnType("DATETIME");
        }
    }
}
